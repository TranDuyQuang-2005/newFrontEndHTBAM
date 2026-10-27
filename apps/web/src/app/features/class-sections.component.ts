import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Subscription, forkJoin } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { AcademicConfiguration, Room } from '../core/models';
import { ModalComponent, RowActionMenuComponent, errorText } from '../shared/ui';
import { SearchSelectComponent } from '../shared/search-select.component';
import { DateInputComponent } from '../shared/date-input.component';
import { ClassSectionImportComponent } from './class-section-import.component';
import { CatalogSectionHeaderComponent } from '../shared/catalog-section-header.component';

@Component({
  selector: 'app-class-sections',
  standalone: true, changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, DateInputComponent, ClassSectionImportComponent, ModalComponent, CatalogSectionHeaderComponent, RowActionMenuComponent, SearchSelectComponent],
  template: `
    <app-catalog-section-header title="Lớp học phần" description="Lịch học, danh sách sinh viên và quyền theo từng lớp.">
      @if (auth.hasPermission('CLASS_SECTION_CREATE')) { <button type="button" (click)="openEditor()">+ Thêm lớp học phần</button> }
      @if (auth.hasPermission('CLASS_SECTION_CREATE')) { <button type="button" class="secondary" (click)="importOpen.set(true)">Nhập Excel</button> }
    </app-catalog-section-header>
    @if(importOpen()){<app-class-section-import [config]="config()" (close)="importOpen.set(false)" (imported)="load()" />}
    @if (error()) { <div class="error-box" role="alert">{{ error() }}</div> }
    @if (message()) { <div class="success-box" role="status">{{ message() }}</div> }
    <div class="filter-bar">
      <label class="grow">Bộ môn<app-search-select resource="departments" label="Lọc bộ môn" [(ngModel)]="departmentId" (ngModelChange)="page = 1; load()" /></label>
      <label class="grow">Tìm lớp học phần<app-search-select resource="class-sections" label="Tìm lớp học phần" [ngModel]="null" (ngModelChange)="showDetail($event)" [filters]="{departmentId: departmentId}" /></label>
      <button type="button" class="secondary" (click)="load()">Tải lại</button>
    </div>
    @if (loading()) { <div class="notice" role="status">Đang tải lớp học phần…</div> }
    <section class="card table-card"><div class="table-wrap"><table>
      <thead><tr><th>Mã / Tên</th><th>Môn học</th><th>Bộ môn</th><th>Giảng viên</th><th>Năm học / Học kỳ</th><th>Trạng thái</th><th>Thao tác</th></tr></thead>
      <tbody>@for (entry of rows(); track entry.data.id) {
        <tr class="clickable-row" tabindex="0" [attr.aria-label]="'Xem lớp ' + entry.data.name" (click)="rowClick($event, entry.data.id)" (keydown.enter)="rowKey($event, entry.data.id)">
          <td class="cell-title">{{entry.data.code}}<small>{{entry.data.name}}</small>@if(entry.data.sourceClassSectionCode){<small>Mã nguồn: {{entry.data.sourceClassSectionCode}}</small>}</td>
          <td>{{entry.data.course}}<small>{{entry.data.credits}} tín chỉ</small></td><td>{{entry.data.department}}</td><td>{{entry.data.teacher}}</td>
          <td>{{entry.data.academicYear}} · HK{{entry.data.semester}}<small>{{entry.data.phase ? 'Giai đoạn ' + entry.data.phase : 'Nguyên kỳ / dữ liệu cũ'}}</small></td>
          <td><span class="badge" [class.gray]="!entry.data.isActive">{{entry.data.isActive ? 'Hoạt động' : 'Ngừng hoạt động'}}</span></td>
          <td class="menu-cell"><app-row-action-menu>
            <button type="button" (click)="showDetail(entry.data.id)">Xem chi tiết</button>
            <button type="button" (click)="openRoster(entry.data.id)">Danh sách sinh viên</button>
            @if(entry.data.isActive && canEdit(entry.permissions)){<button type="button" (click)="openEditor(entry.data.id)">Sửa chi tiết / lịch</button>}
            @if(auth.hasPermission('CLASS_SECTION_GRANT')){<button type="button" (click)="openGrants(entry.data.id)">Cấp / thu hồi quyền</button>}
            @if(entry.data.isActive && entry.permissions.canDeactivate){<button type="button" class="danger" (click)="pendingDeactivate.set(entry.data)">Ngừng lớp học phần</button>}
          </app-row-action-menu></td>
        </tr>
      } @empty { <tr><td colspan="7" class="empty">{{loading() ? 'Đang tải…' : 'Không có lớp học phần trong phạm vi được phép.'}}</td></tr> }</tbody>
    </table></div><div class="pagination"><span>{{total()}} lớp · Trang {{page}}</span><div class="actions"><button type="button" class="secondary small" [disabled]="page <= 1 || loading()" (click)="page = page - 1; load()">Trước</button><button type="button" class="secondary small" [disabled]="page * pageSize >= total() || loading()" (click)="page = page + 1; load()">Sau</button></div></div></section>

    @if(detail()){<app-modal title="Thông tin lớp học phần" size="lg" (close)="detail.set(null)">
      <dl class="details-grid">
        <div><dt>Mã nội bộ</dt><dd>{{detail().code}}</dd></div><div><dt>Mã lớp nguồn</dt><dd>{{detail().sourceClassSectionCode || 'Không có'}}</dd></div>
        <div><dt>Tên lớp</dt><dd>{{detail().name}}</dd></div><div><dt>Môn / tín chỉ</dt><dd>{{detail().course}} · {{detail().credits}} TC</dd></div>
        <div><dt>Khoa</dt><dd>{{detail().faculty}}</dd></div><div><dt>Bộ môn</dt><dd>{{detail().department}}</dd></div>
        <div><dt>Giảng viên</dt><dd>{{detail().teacherCode}} · {{detail().teacher}}</dd></div><div><dt>Năm học / học kỳ / giai đoạn</dt><dd>{{detail().academicYear}} · HK{{detail().semester}} · {{detail().phase ? 'GĐ ' + detail().phase : 'Nguyên kỳ / dữ liệu cũ'}}</dd></div>
        <div><dt>Khoảng ngày học kỳ</dt><dd>{{date(detail().term?.startsOn)}} – {{date(detail().term?.endsOn)}}</dd></div><div><dt>Trạng thái</dt><dd>{{detail().isActive ? 'Hoạt động' : 'Ngừng hoạt động'}}</dd></div>
        <div><dt>Khoảng ngày lựa chọn</dt><dd>{{date(detail().dateRange?.startsOn)}} – {{date(detail().dateRange?.endsOn)}}</dd></div>
        <div><dt>Ghi chú</dt><dd>{{detail().note || '—'}}</dd></div>
      </dl>
      <h3>Các dòng lịch</h3>
      @for(line of detail().schedules; track line.id){<article class="schedule-line" style="margin-bottom:12px"><strong>{{day(line.isoDayOfWeek)}} · tiết {{line.startPeriod}}–{{line.endPeriod}} · {{line.activityType}}</strong><p>{{line.roomCode}} · {{line.room}} · Nhóm {{line.bellGroup}}</p><p>{{date(line.startsOn)}} – {{date(line.endsOn)}}</p><p>Tuần: {{line.selectedWeeks.length ? line.selectedWeeks.join(', ') : 'Mọi tuần trong khoảng ngày'}}</p><div class="schedule-time">{{timeSummary(line)}}</div></article>}
      @empty{<div class="notice">Lớp dữ liệu cũ chưa có dòng lịch chuẩn hóa; dữ liệu buổi học cũ được giữ nguyên.</div>}
      @for(item of detail().exceptions; track item.id){<p class="notice">Nghỉ {{date(item.date)}}: {{item.reason}}</p>}
      <div class="modal-footer" style="margin:20px -20px -20px"><button type="button" class="secondary" (click)="detail.set(null)">Đóng</button></div>
    </app-modal>}

    @if(editorOpen()){<app-modal [title]="editingId ? 'Sửa lớp học phần và lịch' : 'Thêm lớp học phần'" size="lg" [busy]="saving()" (close)="closeEditor()">
      @if(editorError()){<div class="error-box" role="alert">{{editorError()}}</div>}
      <form class="stack" (ngSubmit)="previewChanges()" (input)="invalidatePreview()" (change)="invalidatePreview()">
        <div class="form-grid">
          <label>Mã nội bộ<input [value]="form.code || 'Tự động tạo khi lưu'" readonly /></label>
          <label>Tên lớp học phần<input name="name" [(ngModel)]="form.name" required [disabled]="!manager() && !permissions.canEditDetails" /></label>
          <label>Môn học<app-search-select name="courseId" resource="courses" label="Môn học" [(ngModel)]="form.courseId" [disabled]="!manager() || !!form.hasProtectedSessions" (ngModelChange)="courseChanged()" /></label>
          <label>Giảng viên<app-search-select name="teacherId" resource="teachers" label="Giảng viên" [(ngModel)]="form.teacherId" [disabled]="!manager()" [filters]="{departmentId: selectedCourse()?.departmentId}" (ngModelChange)="invalidatePreview()" /></label>
          <label>Năm học<select name="academicYear" [(ngModel)]="form.academicYear" [disabled]="!manager() || !!form.hasProtectedSessions" (ngModelChange)="yearChanged()"><option value="">Chọn năm học</option>@for(year of academicYears(); track year){<option [value]="year">{{year}}</option>}</select></label>
          <label>Học kỳ<select name="termId" [(ngModel)]="form.academicTermId" [disabled]="!manager() || !!form.hasProtectedSessions" (ngModelChange)="dependenciesChanged()"><option [ngValue]="null">Chọn học kỳ</option>@for(term of termsForYear(); track term.id){<option [ngValue]="term.id">Học kỳ {{term.semester}}{{term.isDevelopment ? ' · DEMO' : term.datesConfirmed ? '' : ' · chưa đủ cấu hình'}}</option>}</select></label>
          <label>Ngày học kỳ<input [value]="date(selectedTerm()?.startsOn) + ' – ' + date(selectedTerm()?.endsOn)" readonly /></label>
          <div>Tuần 1 bắt đầu: {{weekAnchor() ? date(weekAnchor()) : 'Chưa cấu hình Mốc bắt đầu Tuần 1 cho học kỳ này'}} @if(selectedTerm()?.isDevelopment){<span class="badge warning">Mốc tuần mẫu</span>}<br><a routerLink="/academic-configuration">Cấu hình học vụ</a></div>
          <label>Giai đoạn<select name="phase" [(ngModel)]="form.phase" [disabled]="!manager() || !!form.hasProtectedSessions || selectedCourse()?.credits === 3" (ngModelChange)="refreshRange(true)"><option [ngValue]="null">{{selectedCourse()?.credits === 3 ? 'Nguyên kỳ (3 tín chỉ)' : 'Chọn giai đoạn'}}</option>@for(n of phaseOptions(); track n){<option [ngValue]="n">Giai đoạn {{n}}</option>}</select></label>
          <label>Chính sách chuyên cần<app-search-select name="policy" resource="policies" label="Chính sách chuyên cần" [(ngModel)]="form.attendancePolicyId" [disabled]="!manager()" (ngModelChange)="invalidatePreview()" /></label>
          <label class="span2">Ghi chú<textarea name="note" [(ngModel)]="form.note" [disabled]="!manager() && !permissions.canEditDetails"></textarea></label>
        </div>
        @if(range()){<div class="notice">Khoảng ngày lựa chọn: {{date(range()!.startsOn)}} – {{date(range()!.endsOn)}}. Số tiết không được tự suy ra từ tín chỉ.</div>}
        @if(rangeError()){<div class="error-box">{{rangeError()}}</div>}
        <div class="schedule-lines">
          @for(line of form.schedules; track line.lineKey; let i = $index){<section class="schedule-line"><div class="schedule-line-header"><h4>Lịch {{i + 1}}</h4>@if(manager() || permissions.canDeleteDetails){<button type="button" class="secondary small" (click)="removeLine(i)">Bỏ dòng</button>}</div>
            <fieldset class="form-grid schedule-fields" [disabled]="!lineEditable(line)">
              <label>Thứ<select [name]="'day' + i" [(ngModel)]="line.isoDayOfWeek"><option [ngValue]="1">Thứ Hai</option><option [ngValue]="2">Thứ Ba</option><option [ngValue]="3">Thứ Tư</option><option [ngValue]="4">Thứ Năm</option><option [ngValue]="5">Thứ Sáu</option><option [ngValue]="6">Thứ Bảy</option><option [ngValue]="7">Chủ nhật</option></select></label>
              <label>Loại lịch<select [name]="'activity' + i" [(ngModel)]="line.activityType"><option value="LT">LT</option><option value="TH">TH</option></select></label>
              <label>Phòng<app-search-select [name]="'room' + i" resource="rooms" label="Phòng học" [(ngModel)]="line.roomId" [selectedLabel]="line.room" (ngModelChange)="roomChanged(line)" /></label>
              <label>Nhóm giờ<select [name]="'group' + i" [(ngModel)]="line.bellGroup" [disabled]="!!derivedGroup(line)"><option [ngValue]="null">Cần chọn nhóm giờ</option><option value="A">A · Lý thuyết / Zoom</option><option value="B">B · Máy tính / Thực hành / TN</option></select></label>
              <label>Tiết bắt đầu<select [name]="'start' + i" [(ngModel)]="line.startPeriod" (ngModelChange)="startPeriodChanged(line)">@for(n of periods; track n){<option [ngValue]="n">{{n}} · {{periodLabel(line,n)}}</option>}</select></label>
              <label>Tiết kết thúc<select [name]="'end' + i" [(ngModel)]="line.endPeriod"><option [ngValue]="null">Chọn tiết kết thúc</option>@for(n of endPeriods(line); track n){<option [ngValue]="n">{{n}} · {{periodLabel(line,n)}}</option>}</select></label>
              <label>Từ ngày<app-date-input [name]="'from' + i" label="Từ ngày (dd/MM/yyyy)" [(ngModel)]="line.startsOn" /></label>
              <label>Đến ngày<app-date-input [name]="'to' + i" label="Đến ngày (dd/MM/yyyy)" [(ngModel)]="line.endsOn" /></label>
              <label class="span2">Các tuần có lịch<select [name]="'weeksMode' + i" [(ngModel)]="line.specificWeeks" (ngModelChange)="weeksModeChanged(line)"><option [ngValue]="false">Học tất cả tuần</option><option [ngValue]="true">Chỉ học một số tuần</option></select></label>
              @if(line.specificWeeks){<div class="week-chips span2" role="group" aria-label="Các tuần có lịch">@for(n of weeksForLine(line); track n){<label class="check"><input type="checkbox" [name]="'week' + i + '-' + n" [ngModel]="line.selectedWeeks.includes(n)" (ngModelChange)="toggleWeek(line,n,$event)" />T{{n}}</label>}@if(!weekAnchor()){<p class="error-box">Chưa cấu hình Mốc bắt đầu Tuần 1 cho học kỳ này. <a routerLink="/academic-configuration">Cấu hình học vụ</a></p>}</div>}
              @if(line.weekCalendarId){<p class="notice span2">Dòng lịch cũ sử dụng mốc tuần riêng đã lưu. Hệ thống giữ nguyên mốc để bảo toàn lịch sử.</p>}
              <div class="schedule-time span2">{{timeSummary(line)}}</div>
            </fieldset>
          </section>}
        </div>
        @if(manager() || permissions.canAddDetails){<button type="button" class="secondary" [disabled]="!range()" (click)="addLine()">+ Thêm dòng lịch</button>}
        @if(preview()){<section class="card"><h3>Ảnh hưởng trước khi áp dụng</h3><p>Tạo {{preview().createCount}} buổi; thay {{preview().replaceCount}} buổi tương lai chưa có dữ liệu; giữ {{preview().preserveCount}} buổi đã diễn ra/đang chạy/có dữ liệu.</p>
          <div class="table-wrap" style="max-height:220px"><table><thead><tr><th>Buổi bị thay</th><th>Thời gian</th><th>Trạng thái</th></tr></thead><tbody>@for(s of preview().affectedSessions; track s.id){<tr><td>#{{s.id}}</td><td>{{instant(s.scheduledStart)}}</td><td>{{s.status}}</td></tr>}@empty{<tr><td colspan="3" class="empty">Không thay buổi cũ.</td></tr>}</tbody></table></div>
          @if(preview().preservedSessionIds.length){<p class="field-hint">Giữ nguyên các buổi: {{preview().preservedSessionIds.join(', ')}}</p>}
        </section>}
        <div class="modal-footer" style="margin:0 -20px -20px"><button type="button" class="secondary" [disabled]="saving()" (click)="closeEditor()">Hủy</button><button type="submit" class="secondary" [disabled]="saving() || previewing()">{{previewing() ? 'Đang kiểm tra…' : 'Xem trước lịch / ảnh hưởng'}}</button><button type="button" [disabled]="saving() || !preview()" (click)="save()">{{saving() ? 'Đang lưu…' : 'Xác nhận và lưu'}}</button></div>
      </form>
    </app-modal>}

    @if(rosterSection()){<app-modal [title]="'Danh sách sinh viên · ' + rosterSection().name" size="lg" [busy]="saving()" (close)="rosterSection.set(null)">
      @if(editorError()){<div class="error-box">{{editorError()}}</div>}
      @if(rosterSection().permissions.canManageStudents){<div class="toolbar"><label class="grow">Thêm sinh viên hiện có<app-search-select resource="students" label="Thêm sinh viên vào lớp" [filters]="{classSectionId: rosterSection().id}" [ngModel]="null" (ngModelChange)="addStudent($event)" /></label></div><p class="field-hint">Chỉ thay đổi thành viên và ghi chú trong lớp này. Hồ sơ sinh viên dùng chung không được sửa tại đây.</p>}
      <div class="table-wrap"><table><thead><tr><th>MSSV</th><th>Họ tên</th><th>Ghi chú trong lớp</th><th>Thao tác</th></tr></thead><tbody>@for(student of roster(); track student.studentId){<tr><td>{{student.studentCode}}</td><td>{{student.fullName}}</td><td>@if(rosterSection().permissions.canManageStudents){<input [attr.aria-label]="'Ghi chú cho ' + student.fullName" [(ngModel)]="student.classNote" />}@else{ {{student.classNote || '—'}} }</td><td class="menu-cell"><app-row-action-menu>@if(rosterSection().permissions.canManageStudents){<button type="button" (click)="saveNote(student)">Lưu ghi chú trong lớp</button><button type="button" class="danger" (click)="removeStudent(student.studentId)">Bỏ khỏi lớp</button>}@else{<button type="button" disabled>Chỉ xem</button>}</app-row-action-menu></td></tr>}@empty{<tr><td colspan="4" class="empty">Lớp chưa có sinh viên.</td></tr>}</tbody></table></div>
      <div class="modal-footer" style="margin:20px -20px -20px"><button type="button" class="secondary" [disabled]="saving()" (click)="rosterSection.set(null)">Đóng</button></div>
    </app-modal>}

    @if(grantSection()){<app-modal title="Quyền giảng viên theo lớp học phần" [busy]="saving()" (close)="grantSection.set(null)">
      <p>{{grantSection().code}} · {{grantSection().name}}</p><p>Giảng viên được phân công: <strong>{{grantSection().teacher}}</strong></p>
      @if(editorError()){<div class="error-box">{{editorError()}}</div>}
      <div class="stack"><label class="check"><input type="checkbox" [(ngModel)]="grant.canAddDetails" />Thêm chi tiết / dòng lịch</label><label class="check"><input type="checkbox" [(ngModel)]="grant.canEditDetails" />Sửa chi tiết / dòng lịch</label><label class="check"><input type="checkbox" [(ngModel)]="grant.canDeleteDetails" />Xóa dòng lịch</label><label class="check"><input type="checkbox" [(ngModel)]="grant.canManageStudents" />Thêm/bỏ sinh viên và sửa ghi chú trong lớp</label></div>
      <p class="notice">Quyền chỉ áp dụng cho lớp này. Giảng viên không được ngừng lớp hoặc sửa hồ sơ sinh viên toàn hệ thống.</p>
      <div class="modal-footer" style="margin:20px -20px -20px"><button type="button" class="secondary" [disabled]="saving()" (click)="grantSection.set(null)">Hủy</button><button type="button" [disabled]="saving()" (click)="saveGrants()">Lưu quyền</button></div>
    </app-modal>}

    @if(pendingDeactivate()){<app-modal title="Ngừng lớp học phần" size="sm" [busy]="saving()" (close)="pendingDeactivate.set(null)"><p>Ngừng lớp {{pendingDeactivate().name}}? Các buổi tương lai chưa có dữ liệu sẽ được hủy; lịch sử và buổi có dữ liệu được giữ nguyên.</p><div class="modal-footer" style="margin:20px -20px -20px"><button type="button" class="secondary" [disabled]="saving()" (click)="pendingDeactivate.set(null)">Hủy</button><button type="button" class="danger" [disabled]="saving()" (click)="deactivate()">Xác nhận ngừng lớp</button></div></app-modal>}
  `,
})
export class ClassSectionsComponent implements OnInit, OnDestroy {
  readonly api = inject(ApiService); readonly auth = inject(AuthService);
  readonly importOpen = signal(false);
  readonly rows = signal<any[]>([]); readonly total = signal(0); readonly loading = signal(false); readonly saving = signal(false);
  readonly error = signal(''); readonly message = signal(''); readonly editorError = signal(''); readonly rangeError = signal('');
  readonly detail = signal<any>(null); readonly editorOpen = signal(false); readonly preview = signal<any>(null); readonly previewing = signal(false);
  readonly range = signal<{startsOn:string;endsOn:string}|null>(null); readonly rosterSection = signal<any>(null); readonly roster = signal<any[]>([]);
  readonly grantSection = signal<any>(null); readonly pendingDeactivate = signal<any>(null);
  readonly config = signal<AcademicConfiguration|null>(null); readonly courses = signal<any[]>([]); readonly rooms = signal<Room[]>([]);
  readonly periods = Array.from({length:16}, (_,i) => i + 1);
  departmentId:number|null = null; page = 1; readonly pageSize = 20; editingId:number|null = null;
  form:any = {}; permissions:any = {}; grant:any = {}; private listRequest?:Subscription; private rangeRequest?:Subscription; private existingKeys = new Set<string>();
  ngOnInit():void {
    this.load();
    forkJoin({config:this.api.academicConfiguration(),courses:this.api.courses(),rooms:this.api.rooms()}).subscribe({next:r=>{this.config.set(r.config);this.courses.set(r.courses);this.rooms.set(r.rooms);if(this.editorOpen())this.refreshRange(false);},error:e=>this.error.set(errorText(e))});
  }
  ngOnDestroy():void {this.listRequest?.unsubscribe();this.rangeRequest?.unsubscribe();}
  load():void {this.listRequest?.unsubscribe();this.loading.set(true);this.error.set('');this.listRequest=this.api.sectionsPage({page:this.page,pageSize:this.pageSize,...(this.departmentId?{departmentId:this.departmentId}:{})}).subscribe({next:r=>{this.rows.set(r.items);this.total.set(r.total);this.loading.set(false);},error:e=>{this.error.set(errorText(e));this.loading.set(false);}});}
  canEdit(p:any):boolean{return !!(p?.canAddDetails||p?.canEditDetails||p?.canDeleteDetails);}
  rowClick(event:MouseEvent,id:number):void{if(!(event.target as Element).closest('button,a,input,select,textarea,app-row-action-menu'))this.showDetail(id);}
  rowKey(event:Event,id:number):void{if(event.target===event.currentTarget){event.preventDefault();this.showDetail(id);}}
  showDetail(id:number|null):void{if(id)this.api.sectionDetail(id).subscribe({next:r=>this.detail.set(r),error:e=>this.error.set(errorText(e))});}
  openEditor(id?:number):void {
    this.editorError.set('');this.preview.set(null);this.range.set(null);this.rangeError.set('');this.editingId=id??null;
    this.existingKeys.clear();
    if(id){this.api.sectionDetail(id).subscribe({next:r=>{this.permissions=r.permissions;this.form={...r,schedules:r.schedules.map((x:any)=>({...x,specificWeeks:x.selectedWeeks.length>0}))};this.existingKeys=new Set(r.schedules.map((x:any)=>x.lineKey));this.editorOpen.set(true);this.refreshRange(false);},error:e=>this.error.set(errorText(e))});}
    else{this.permissions={isManager:true};this.form={name:'',sourceClassSectionCode:'',note:'',courseId:null,teacherId:null,academicYear:'',academicTermId:null,phase:null,attendancePolicyId:null,schedules:[],revision:0,creationKey:crypto.randomUUID().replaceAll('-','')};this.editorOpen.set(true);}
  }
  manager():boolean{return !this.editingId||!!this.permissions.isManager;}
  lineEditable(line:any):boolean{return this.manager() || (this.existingKeys.has(line.lineKey) ? !!this.permissions.canEditDetails : !!this.permissions.canAddDetails);}
  weekCalendars():any[]{return this.config()?.weekCalendars.filter(x=>x.academicTermId===this.form.academicTermId)??[];}
  closeEditor():void{if(this.saving())return;if(this.form.schedules?.length&&!confirm('Đóng form? Các thay đổi chưa lưu sẽ bị bỏ.'))return;this.rangeRequest?.unsubscribe();this.editorOpen.set(false);}
  invalidatePreview():void{this.preview.set(null);}
  selectedCourse():any{return this.courses().find(x=>x.id===this.form.courseId);}
  selectedTerm():any{return this.config()?.terms.find(x=>x.id===this.form.academicTermId);}
  academicYears():string[]{return [...new Set(this.config()?.terms.map(x=>x.academicYear)??[])];}
  termsForYear():any[]{return this.config()?.terms.filter(x=>x.academicYear===this.form.academicYear)??[];}
  phaseOptions():number[]{const credits=this.selectedCourse()?.credits;return credits===1?[1,2,3]:credits===2?[1,2]:[];}
  yearChanged():void{this.form.academicTermId=null;this.dependenciesChanged();}
  courseChanged():void{this.form.teacherId=null;this.dependenciesChanged();}
  dependenciesChanged():void{this.form.phase=null;this.form.schedules=[];this.preview.set(null);this.range.set(null);this.refreshRange(true);}
  refreshRange(reset:boolean):void {
    this.rangeRequest?.unsubscribe();this.range.set(null);this.preview.set(null);this.rangeError.set('');
    const course=this.selectedCourse();if(!course||!this.form.academicTermId)return;
    if(course.credits<1||course.credits>3){this.rangeError.set('Tín chỉ ngoài 1–3 chưa có quy tắc phù hợp.');return;}
    if(course.credits===3)this.form.phase=null;else if(!this.form.phase)return;
    this.rangeRequest=this.api.academicRange(this.form.academicTermId,course.credits,this.form.phase).subscribe({next:r=>{this.range.set(r);if(reset){this.form.schedules=[];this.addLine();}},error:e=>this.rangeError.set(errorText(e))});
  }
  addLine():void{const range=this.range();if(!range)return;this.form.schedules.push({lineKey:crypto.randomUUID().replaceAll('-',''),isoDayOfWeek:1,startPeriod:1,endPeriod:3,bellGroup:null,roomId:null,activityType:'LT',...range,selectedWeeks:[],specificWeeks:false});this.invalidatePreview();}
  removeLine(i:number):void{this.form.schedules.splice(i,1);this.invalidatePreview();}
  derivedGroup(line:any):string|null{const type=this.rooms().find(x=>x.id===line.roomId)?.roomType;return type==='THEORY'||type==='ZOOM'?'A':['COMPUTER','LAB','PRACTICE','PE','DEFENSE'].includes(type??'')?'B':null;}
  roomChanged(line:any):void{line.bellGroup=this.derivedGroup(line);line.activityType=line.bellGroup==='B'?'TH':'LT';this.invalidatePreview();}
  startPeriodChanged(line:any):void{if(!this.endPeriods(line).includes(line.endPeriod))line.endPeriod=null;}
  endPeriods(line:any):number[]{const shift=(n:number)=>n<=6?1:n<=12?2:3;return this.periods.filter(n=>n>=line.startPeriod&&shift(n)===shift(line.startPeriod));}
  setWeeks(line:any,value:string):void{line.selectedWeeks=value.trim()?value.split(',').map(x=>Number(x.trim())):[];this.invalidatePreview();}
  weekAnchor():string|null{const term=this.selectedTerm();return term&&(term.weekMappingConfirmed||term.isDevelopment)?term.weekOneMonday:null;}
  weeksForLine(line:any):number[]{const anchor=this.weekAnchor();if(!anchor||!line.startsOn||!line.endsOn)return [];const start=Date.parse(anchor);const end=Date.parse(line.endsOn);const from=Date.parse(line.startsOn);return Array.from({length:Math.min(53,Math.max(0,Math.floor((end-start)/604800000)+1))},(_,i)=>i+1).filter(n=>start+n*604800000>from);}
  weeksModeChanged(line:any):void{if(!line.specificWeeks)line.selectedWeeks=[];this.invalidatePreview();}
  toggleWeek(line:any,n:number,checked:boolean):void{line.selectedWeeks=checked?[...new Set<number>([...line.selectedWeeks,n])].sort((a,b)=>a-b):line.selectedWeeks.filter((x:number)=>x!==n);this.invalidatePreview();}
  version(line:any):any{return this.config()?.bellVersions.filter(v=>v.group===line.bellGroup&&v.confirmed&&v.effectiveFrom<=line.startsOn&&(!v.effectiveTo||v.effectiveTo>=line.startsOn)).sort((a,b)=>b.effectiveFrom.localeCompare(a.effectiveFrom))[0];}
  periodLabel(line:any,n:number):string{const v=this.version(line);const p=this.config()?.periods.find(p=>p.bellTimeVersionId===v?.id&&p.number===n);return p?`${p.startsAt.slice(0,5)}–${p.endsAt.slice(0,5)}`:'chưa có bảng giờ';}
  timeSummary(line:any):string{if(!line.bellGroup)return 'Cần chọn phòng/nhóm giờ phù hợp; LT/TH không tự quyết định nhóm giờ.';const v=this.version(line);return v?`Nhóm ${line.bellGroup} · tiết ${line.startPeriod}: ${this.periodLabel(line,line.startPeriod)} · tiết ${line.endPeriod??'?'}: ${this.periodLabel(line,line.endPeriod)} · Tổng ${line.endPeriod ? line.endPeriod-line.startPeriod+1 : 0} tiết · ${v.code}. Xem trước để kiểm tra mọi ngày phát sinh.`:'Thiếu bảng giờ có hiệu lực cho khoảng ngày này. Bảng 12/01/2026 không áp dụng ngược cho năm 2025.';}
  payload():any{return {name:this.form.name,courseId:this.form.courseId,teacherId:this.form.teacherId,academicTermId:this.form.academicTermId,phase:this.form.phase,attendancePolicyId:this.form.attendancePolicyId,note:this.form.note||null,sourceClassSectionCode:this.form.sourceClassSectionCode||null,revision:this.form.revision||0,creationKey:this.form.creationKey||null,previewToken:this.preview()?.previewToken??null,schedules:this.form.schedules.map((x:any)=>({lineKey:x.lineKey,isoDayOfWeek:x.isoDayOfWeek,startPeriod:x.startPeriod,endPeriod:x.endPeriod,bellGroup:x.bellGroup,roomId:x.roomId,activityType:x.activityType,startsOn:x.startsOn,endsOn:x.endsOn,selectedWeeks:x.selectedWeeks,weekCalendarId:x.weekCalendarId??null}))};}
  previewChanges():void{if(this.previewing())return;if(!this.range()){this.editorError.set(this.rangeError()||'Cần chọn môn, năm học, học kỳ và giai đoạn; xác nhận ngày học kỳ tại Cấu hình học vụ.');return;}if(this.form.schedules.some((x:any)=>x.specificWeeks&&!x.selectedWeeks.length)){this.editorError.set('Chọn ít nhất một tuần hoặc chọn Học tất cả tuần.');return;}this.previewing.set(true);this.editorError.set('');this.api.previewSection(this.editingId,this.payload()).subscribe({next:r=>{this.preview.set(r);this.previewing.set(false);},error:e=>{this.editorError.set(errorText(e));this.previewing.set(false);}});}
  save():void{if(this.saving()||!this.preview())return;this.saving.set(true);this.editorError.set('');this.api.saveSection(this.editingId,this.payload()).subscribe({next:()=>{this.saving.set(false);this.editorOpen.set(false);this.message.set('Đã lưu lớp, lịch và các buổi học.');this.load();},error:e=>{this.editorError.set(errorText(e));this.saving.set(false);}});}
  openRoster(id:number):void{this.editorError.set('');forkJoin({section:this.api.sectionDetail(id),students:this.api.sectionStudents(id)}).subscribe({next:r=>{this.rosterSection.set(r.section);this.roster.set(r.students);},error:e=>this.error.set(errorText(e))});}
  addStudent(id:number|null):void{if(id&&!this.saving())this.persistRoster([...this.roster().map(x=>x.studentId),id]);}
  removeStudent(id:number):void{if(!this.saving()&&confirm('Bỏ sinh viên khỏi lớp này? Hồ sơ dùng chung vẫn được giữ.'))this.persistRoster(this.roster().filter(x=>x.studentId!==id).map(x=>x.studentId));}
  persistRoster(ids:number[]):void{this.saving.set(true);this.api.setSectionStudents(this.rosterSection().id,ids).subscribe({next:()=>{this.saving.set(false);this.openRoster(this.rosterSection().id);},error:e=>{this.saving.set(false);this.editorError.set(errorText(e));}});}
  saveNote(student:any):void{this.saving.set(true);this.api.setClassStudentNote(this.rosterSection().id,student.studentId,student.classNote??'').subscribe({next:()=>this.saving.set(false),error:e=>{this.saving.set(false);this.editorError.set(errorText(e));}});}
  openGrants(id:number):void{this.editorError.set('');forkJoin({section:this.api.sectionDetail(id),grants:this.api.sectionGrants(id)}).subscribe({next:r=>{this.grant={canAddDetails:false,canEditDetails:false,canDeleteDetails:false,canManageStudents:false,...r.grants.find(x=>x.teacherId===r.section.teacherId)};this.grantSection.set(r.section);},error:e=>this.error.set(errorText(e))});}
  saveGrants():void{this.saving.set(true);const s=this.grantSection();this.api.setSectionGrant(s.id,s.teacherId,this.grant).subscribe({next:()=>{this.saving.set(false);this.grantSection.set(null);this.auth.ensureProfile(true).subscribe();this.load();},error:e=>{this.saving.set(false);this.editorError.set(errorText(e));}});}
  deactivate():void{this.saving.set(true);this.api.deactivateSection(this.pendingDeactivate().id).subscribe({next:()=>{this.saving.set(false);this.pendingDeactivate.set(null);this.load();},error:e=>{this.saving.set(false);this.pendingDeactivate.set(null);this.error.set(errorText(e));}});}
  date(value:string|null|undefined):string{return value?value.slice(0,10).split('-').reverse().join('/'):'Chưa cấu hình';}
  day(value:number):string{return value===7?'Chủ nhật':`Thứ ${value+1}`;}
  instant(value:string):string{return new Intl.DateTimeFormat('vi-VN',{timeZone:'Asia/Ho_Chi_Minh',dateStyle:'short',timeStyle:'short'}).format(new Date(/Z$|[+-]\d\d:\d\d$/.test(value)?value:value+'Z'));}
}
