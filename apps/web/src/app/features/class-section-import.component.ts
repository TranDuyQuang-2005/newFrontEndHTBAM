import { Component, EventEmitter, Input, Output, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { AcademicConfiguration } from '../core/models';
import { ModalComponent, errorText } from '../shared/ui';
import { SearchSelectComponent } from '../shared/search-select.component';
import { ImportColumnChoice, ImportColumnSelectComponent } from '../shared/import-column-select.component';

@Component({
  selector: 'app-class-section-import', standalone: true,
  imports: [FormsModule, RouterLink, ModalComponent, SearchSelectComponent, ImportColumnSelectComponent],
  template: `<app-modal title="Nhập Excel lớp học phần" size="lg" [busy]="busy()" (close)="close.emit()">
    <ol class="import-steps" aria-label="Các bước nhập Excel">@for(label of steps; track $index){<li [class.active]="step() === $index + 1" [attr.aria-current]="step() === $index + 1 ? 'step' : null">{{$index + 1}}. {{label}}</li>}</ol>
    <p class="field-hint">Hỗ trợ .xls/.xlsx, tối đa 10 MB, 20.000 dòng và 64 cột. Đọc và kiểm tra toàn bộ trên máy chủ; mỗi trang xem trước tối đa 200 dòng.</p>
    @if(error()){<div class="error-box" role="alert">{{error()}}</div>}
    @if(busy()){<p class="notice" role="status">Đang đọc và kiểm tra dữ liệu, vui lòng chờ…</p>}
    @if(step() === 1){
      <section class="import-drop-zone" (dragover)="$event.preventDefault()" (drop)="dropFile($event)">
        <label>Chọn hoặc kéo thả file Excel<input type="file" accept=".xls,.xlsx" [disabled]="busy()" (change)="selectFile($event)" /></label>
      </section>
      <button type="button" class="secondary" [disabled]="busy()" (click)="downloadTemplate()">Tải file mẫu</button>
    }
    @if(file){<p class="import-file-name"><strong>{{file.name}}</strong> · {{size()}} MB · {{sheets().length}} sheet @if(inspection()){· {{inspection().rowCount.toLocaleString('vi-VN')}} dòng · {{inspection().columnCount}} cột}</p>}
    @if(step() === 2){
      <label>Sheet<select [(ngModel)]="sheet" [disabled]="busy()" (ngModelChange)="chooseSheet()">@for(name of sheets(); track name){<option [value]="name">{{name}}</option>}</select></label>
      @if(inspection()){<div class="notice" role="status"><p>{{inspection().formatMessage}}</p>@if(inspection().format !== 'UNKNOWN' && !headerDraftChanged()){<p>Tiêu đề: dòng {{headerStartRow}}–{{headerEndRow()}}. Dữ liệu bắt đầu: dòng {{dataStartRow()}}.</p>}</div>}
      <button type="button" class="secondary" [disabled]="busy()" [attr.aria-expanded]="manualRecognition" (click)="toggleManualRecognition()">{{manualRecognition ? 'Đóng chỉnh nhận diện thủ công' : 'Chỉnh nhận diện thủ công'}}</button>
      @if(manualRecognition){<section class="import-manual-recognition"><p class="field-hint">Giá trị bên dưới là bản chỉnh. Bấm Áp dụng nhận diện để cập nhật tiêu đề và toàn bộ ánh xạ.</p><div class="form-grid">
        <label>Dòng bắt đầu tiêu đề<input type="number" min="1" max="20" [(ngModel)]="draftHeaderStartRow" [disabled]="busy()" /></label>
        <label>Số dòng tiêu đề<select [(ngModel)]="draftHeaderRowCount" [disabled]="busy()"><option [ngValue]="1">Một dòng</option><option [ngValue]="2">Hai dòng</option></select></label>
      </div><button type="button" class="secondary" [disabled]="busy() || !validHeaderDraft()" (click)="inspectSheet(false)">Áp dụng nhận diện</button>
      @if(headerDraftChanged()){<p class="notice">Bản chỉnh chưa áp dụng. Hãy nhận diện lại trước khi tiếp tục.</p>}</section>}
    }
    @if(step() === 3){
      <h4>Ánh xạ cột <small>* bắt buộc</small></h4>
      <p class="field-hint">Tiêu đề: dòng {{headerStartRow}}–{{headerEndRow()}} · Dữ liệu bắt đầu: dòng {{dataStartRow()}}.</p>
      <h5>Ánh xạ cột đơn</h5>
      <div class="table-wrap import-mapping"><table><thead><tr><th>Trường hệ thống</th><th>Tên cột — cột Excel — dữ liệu mẫu</th></tr></thead><tbody>
        @for(mapping of singleMappings(); track mapping.field){<tr><td>{{mapping.label}}{{mapping.required ? ' *' : ''}}</td><td><app-import-column-select [label]="mapping.label" [columns]="mappingChoices(mapping)" [value]="mapping.columnIndex" [disabled]="busy()" (valueChange)="mapColumn(mapping,$event)" /></td></tr>}
      </tbody></table></div>
      @if(weekMappings().length){<section class="import-week-mapping"><h5>Ánh xạ nhóm cột · Các tuần có lịch</h5><p class="notice">{{weekMappingSummary()}}</p><details><summary>Xem chi tiết từng cột tuần</summary><ul class="import-week-columns">@for(mapping of weekMappings(); track mapping.field){<li>{{columnLabel(mapping.columnIndex)}}</li>}</ul></details><p class="field-hint">x, X, 1, true hoặc dấu ✓/✔/☑/√: tuần được chọn; ô trống: tuần không được chọn.</p></section>}
      <p class="field-hint">Cần Mã môn hoặc Tên môn học, Mã GV hoặc Giảng viên. Hệ thống không đoán mã môn từ Mã LHP và không tự tạo danh mục.</p>
      <div class="form-grid">
        <label>Năm học / Học kỳ<select [(ngModel)]="termId" [disabled]="busy()"><option [ngValue]="null">Chọn học kỳ</option>@for(term of config?.terms; track term.id){<option [ngValue]="term.id">{{term.academicYear}} · HK{{term.semester}}{{term.isDevelopment ? ' · DEMO' : ''}}</option>}</select></label>
        <label>Chính sách chuyên cần<app-search-select [boundToModal]="true" resource="policies" label="Chính sách chuyên cần" [(ngModel)]="policyId" [disabled]="busy()" /></label>
        <label>Giai đoạn mặc định (môn 1/2 TC)<select [(ngModel)]="phase" [disabled]="busy()"><option [ngValue]="null">Theo cột Giai đoạn</option>@for(n of [1,2,3]; track n){<option [ngValue]="n">Giai đoạn {{n}}</option>}</select></label>
      </div>
      <h4>Xem nhanh {{quickRows().length}} dòng đầu theo ánh xạ</h4>
      <p class="field-hint">Dữ liệu nguồn để kiểm tra cột. Hệ thống kiểm tra toàn bộ và đối chiếu danh mục ở bước Xem trước.</p>
      <div class="table-wrap import-quick-preview"><table><thead><tr><th>Dòng / Mã LHP</th><th>Tên môn</th><th>Loại / Thứ / Tiết</th><th>Phòng</th><th>Bắt đầu / Kết thúc</th><th>Các tuần được chọn</th></tr></thead><tbody>
        @for(row of quickRows(); track row.row){<tr><td>{{row.row}} · {{row.source}}</td><td>{{row.course}}</td><td>{{row.activity}} · {{row.day}}<br>{{row.periods}}</td><td>{{row.room}}</td><td>{{row.from}}<br>{{row.to}}</td><td>{{row.weeks}}</td></tr>}
      </tbody></table></div>
    }
    @if((step() === 4 || step() === 5) && preview(); as p){
      <p class="notice">{{p.weekMessage}} @if(p.isDevelopment){<strong> · Mốc tuần mẫu</strong>}</p>
      @if(!p.weekOneMonday){<a routerLink="/academic-configuration">Mở Cấu hình học vụ</a>}
      <p role="status">File {{p.workbookRows.toLocaleString('vi-VN')}} dòng, {{p.workbookColumns}} cột; {{p.dataRows.toLocaleString('vi-VN')}} dòng dữ liệu và {{p.totalGroups.toLocaleString('vi-VN')}} nhóm Mã LHP.</p>
      <div class="notice">Sẽ tạo {{p.classesToCreate}} lớp, {{p.schedulesToCreate}} dòng lịch. Bỏ qua {{p.skippedRows}} dòng; cảnh báo {{p.warningRows}} dòng; lỗi {{p.errorRows}} dòng; trùng {{p.duplicates}} dòng.</div>
      @if(p.unresolvedCourseRows || p.unresolvedTeacherRows || p.unresolvedRoomRows){<p class="notice">Chưa đối chiếu được: môn ở {{p.unresolvedCourseRows}} dòng, giảng viên ở {{p.unresolvedTeacherRows}} dòng, phòng ở {{p.unresolvedRoomRows}} dòng. Mở nhóm lớp để chọn lại danh mục rồi kiểm tra lại ánh xạ.</p>}
      @if(step() === 4){
        <div class="filter-bar"><label class="grow">Tìm mã nguồn, môn, giảng viên hoặc lỗi<input [(ngModel)]="query" (keydown.enter)="loadPage(1)" /></label><label>Trạng thái<select [(ngModel)]="statusFilter" (ngModelChange)="loadPage(1)"><option value="ALL">Tất cả</option><option value="VALID">Hợp lệ</option><option value="WARNING">Cảnh báo</option><option value="ERROR">Lỗi</option><option value="DUPLICATE">Trùng</option></select></label><button type="button" class="secondary" [disabled]="busy()" (click)="loadPage(1)">Tìm</button><button type="button" class="secondary" [disabled]="busy()" (click)="downloadValidation()">Tải báo cáo kiểm tra CSV</button></div>
        @if(dirtyMappings){<p class="notice warning">Ánh xạ đã thay đổi. Bấm Kiểm tra lại ánh xạ để tạo bản xem trước mới trước khi xác nhận.</p>}
        @for(group of p.groups; track group.key){
          <details class="schedule-line import-group"><summary>{{group.sourceClassSectionCode || '(thiếu Mã LHP)'}} · {{status(group.status)}} · {{group.totalRows}} dòng · {{group.rows[0]?.courseName}}</summary>
            @if(group.message){<p>{{group.message}}</p>}
            <label>Chọn/đổi môn cho nhóm này<app-search-select [boundToModal]="true" resource="courses" label="Môn học đối chiếu" [ngModel]="courseSelections[group.sourceClassSectionCode] || group.rows[0]?.courseId || null" [selectedLabel]="group.rows[0]?.courseName || ''" [disabled]="busy()" (ngModelChange)="selectCourse(group.sourceClassSectionCode,$event)" /></label>
            <div class="table-wrap"><table><thead><tr><th>Dòng / Trạng thái</th><th>Môn / Giảng viên / Phòng</th><th>Lịch / Tiết / Giờ</th><th>Ngày / Các tuần có lịch</th><th>Lỗi / Cảnh báo</th></tr></thead><tbody>
              @for(row of group.rows; track row.row){<tr><td>{{row.row}} · {{status(row.status)}}</td><td>{{row.course}} · {{row.courseName}}<br>{{row.credits}} TC · {{row.className}}<br>{{row.teacher}} · {{row.teacherName}}<br>{{row.room}} · {{row.roomName}}
                @if(!row.teacherId){<app-search-select [boundToModal]="true" resource="teachers" label="Đối chiếu giảng viên dòng Excel" [ngModel]="teacherSelections[row.row] || null" (ngModelChange)="selectTeacher(row.row,$event)" />}
                @if(!row.roomId){<app-search-select [boundToModal]="true" resource="rooms" label="Đối chiếu phòng dòng Excel" [ngModel]="roomSelections[row.row] || null" (ngModelChange)="selectRoom(row.row,$event)" />}
              </td><td>{{row.schedule?.activityType}} · {{row.schedule?.isoDayOfWeek === 7 ? 'CN' : 'Thứ ' + (row.schedule?.isoDayOfWeek + 1)}}<br>Tiết {{row.schedule?.startPeriod}}–{{row.schedule?.endPeriod}}<br>{{row.startsAt}}–{{row.endsAt}}<small>Số tiết nguồn theo dòng: {{row.sourceSchedulePeriods ?? '—'}} · Tổng số tiết nguồn: {{row.sourceTotalPeriods ?? '—'}}</small></td><td>{{date(row.schedule?.startsOn)}}–{{date(row.schedule?.endsOn)}}<div class="week-chips">@for(week of row.weeks; track week.number){<span class="badge" [title]="'T' + week.number + ': ' + date(week.startsOn) + ' – ' + date(week.endsOn)">T{{week.number}} @if(week.startsOn){<small>{{date(week.startsOn)}}</small>}</span>}</div>@if(!row.weeks?.length){<small>{{p.format === 'HUIT' ? 'Không có tuần được đánh dấu' : 'Học tất cả tuần'}}</small>}</td><td>{{row.message}}</td></tr>}
            </tbody></table></div>
          </details>
        } @empty {<p class="notice">Không có dòng phù hợp bộ lọc.</p>}
        <div class="pagination"><span>Trang {{p.page}} · {{p.matchingRows}} dòng / {{p.matchingGroups}} nhóm phù hợp</span><div class="actions"><label>Số dòng<select [(ngModel)]="pageSize" (ngModelChange)="loadPage(1)"><option [ngValue]="100">100</option><option [ngValue]="200">200</option></select></label><button type="button" class="secondary" [disabled]="busy() || p.page <= 1" (click)="loadPage(p.page - 1)">Trước</button><button type="button" class="secondary" [disabled]="busy() || p.page * p.pageSize >= p.matchingRows" (click)="loadPage(p.page + 1)">Sau</button></div></div>
      }
      @if(step() === 5){
        @if(p.errorRows){<p class="error-box">Còn lỗi bắt buộc; không được xác nhận. Quay lại xem trước để xử lý.</p>}
        <label class="check"><input type="checkbox" [(ngModel)]="confirmed" [disabled]="busy() || p.errorRows > 0 || dirtyMappings" />Tôi đã kiểm tra và xác nhận ghi đúng bản xem trước. Lớp nguồn đã tồn tại không được cập nhật.</label>
      }
    }
    @if(step() === 6 && result()){
      <div class="success-box" role="status">Đã tạo {{result().createdClasses}} lớp và {{result().createdSchedules}} dòng lịch; cập nhật {{result().updatedClasses}} lớp. Bỏ qua {{result().skippedRows}} dòng; trùng {{result().duplicates}} dòng.</div>
      @for(item of resultClasses(); track item.id){<p>{{item.code}} · Mã lớp nguồn: {{item.sourceClassSectionCode}}</p>}
      @for(issue of resultErrors(); track issue.row){<p class="error-box">Dòng {{issue.row}} · {{issue.sourceClassSectionCode}} · {{issue.column}}: {{issue.message}}</p>}
      <div class="actions"><button type="button" class="secondary" [disabled]="resultPage <= 1" (click)="resultPage = resultPage - 1">Trước</button><span>Trang {{resultPage}}</span><button type="button" class="secondary" [disabled]="resultPage * 100 >= result().classes.length && resultPage * 100 >= result().errors.length" (click)="resultPage = resultPage + 1">Sau</button><button type="button" class="secondary" [disabled]="busy()" (click)="downloadResult()">Tải kết quả CSV</button></div>
    }
    <div class="modal-footer import-footer"><button type="button" class="secondary" [disabled]="busy()" (click)="close.emit()">Đóng</button>
      @if(step() > 1 && step() < 6){<button type="button" class="secondary" [disabled]="busy()" (click)="back()">Quay lại</button>}
      @if(step() === 1){<button type="button" [disabled]="busy() || !file || !sheets().length" (click)="step.set(2)">Tiếp tục</button>}
      @if(step() === 2){<button type="button" [disabled]="busy() || !sheet || headerDraftChanged() || inspection()?.format === 'UNKNOWN'" (click)="step.set(3)">Tiếp tục ánh xạ cột</button>}
      @if(step() === 3){<button type="button" [disabled]="busy() || !termId || !policyId || !mappingComplete()" (click)="showPreview()">Tiếp tục xem trước</button>}
      @if(step() === 4){<button type="button" class="secondary" [disabled]="busy()" (click)="showPreview()">Kiểm tra lại ánh xạ</button><button type="button" [disabled]="busy() || !preview() || preview().errorRows > 0 || dirtyMappings" (click)="step.set(5)">Tiếp tục xác nhận</button>}
      @if(step() === 5){<button type="button" [disabled]="busy() || !confirmed || preview()?.errorRows > 0 || dirtyMappings" (click)="confirmImport()">Xác nhận nhập</button>}
    </div>
  </app-modal>`,
})
export class ClassSectionImportComponent {
  private readonly api = inject(ApiService);
  @Input() config: AcademicConfiguration | null = null;
  @Output() close = new EventEmitter<void>(); @Output() imported = new EventEmitter<void>();
  readonly steps = ['Chọn file','Sheet / Định dạng','Ánh xạ cột','Xem trước','Xác nhận','Kết quả'];
  readonly step = signal(1); readonly busy = signal(false); readonly error = signal(''); readonly sheets = signal<string[]>([]);
  readonly inspection = signal<any>(null); readonly preview = signal<any>(null); readonly result = signal<any>(null);
  file: File | null = null; sheet = ''; termId: number | null = null; policyId: number | null = null; phase: number | null = null;
  confirmed = false; headerStartRow = 1; headerRowCount = 1; mappings:any[]=[]; private sheetDetails:any[]=[];
  manualRecognition = false; draftHeaderStartRow = 1; draftHeaderRowCount = 1;
  courseSelections:Record<string,number>={};teacherSelections:Record<number,number>={};roomSelections:Record<number,number>={};
  dirtyMappings=false;query='';statusFilter='ALL';pageSize=100;resultPage=1;
  invalidate(): void { this.preview.set(null); this.confirmed = false; }
  size():string{return ((this.file?.size??0)/1048576).toFixed(2);}
  selectFile(event: Event): void { this.acceptFile((event.target as HTMLInputElement).files?.[0]); }
  dropFile(event:DragEvent):void{event.preventDefault();if(!this.busy())this.acceptFile(event.dataTransfer?.files[0]);}
  private acceptFile(file?:File):void{
    if(this.busy())return;this.invalidate();this.error.set('');this.sheets.set([]);this.sheet='';this.file=null;this.inspection.set(null);this.result.set(null);this.step.set(1);this.courseSelections={};this.teacherSelections={};this.roomSelections={};this.dirtyMappings=false;
    if(!file)return;
    if(!/\.(xls|xlsx)$/i.test(file.name)||!file.size||file.size>10*1024*1024){this.error.set(`File ${file.name} có ${(file.size/1048576).toFixed(2)} MB. Chọn .xls/.xlsx có dữ liệu; giới hạn dung lượng là 10 MB.`);return;}
    this.file=file;this.busy.set(true);
    this.api.sectionImportSheets(file).subscribe({next:r=>{this.sheets.set(r.sheets);this.sheetDetails=r.sheetDetails??[];this.sheet=r.sheets[0]??'';this.chooseSheet();this.busy.set(false);},error:e=>{this.error.set(errorText(e));this.busy.set(false);}});
  }
  chooseSheet():void{this.invalidate();this.error.set('');this.courseSelections={};this.teacherSelections={};this.roomSelections={};this.dirtyMappings=false;this.manualRecognition=false;this.applyInspection(this.sheetDetails.find(x=>x.name===this.sheet)??null);}
  private applyInspection(detail:any):void{this.inspection.set(detail);this.headerStartRow=detail?.headerStartRow??detail?.headerRow??1;this.headerRowCount=detail?.headerRowCount??1;this.draftHeaderStartRow=this.headerStartRow;this.draftHeaderRowCount=this.headerRowCount;this.mappings=(detail?.mappings??[]).map((x:any)=>({...x}));}
  headerEndRow():number{return this.headerStartRow+this.headerRowCount-1;}
  dataStartRow():number{return this.headerEndRow()+1;}
  toggleManualRecognition():void{this.manualRecognition=!this.manualRecognition;this.draftHeaderStartRow=this.headerStartRow;this.draftHeaderRowCount=this.headerRowCount;}
  validHeaderDraft():boolean{return Number.isInteger(this.draftHeaderStartRow)&&this.draftHeaderStartRow>=1&&this.draftHeaderStartRow<=20&&[1,2].includes(this.draftHeaderRowCount);}
  headerDraftChanged():boolean{return this.manualRecognition&&(this.draftHeaderStartRow!==this.headerStartRow||this.draftHeaderRowCount!==this.headerRowCount);}
  inspectSheet(advance=false):void{if(!this.file||this.busy()||!this.validHeaderDraft())return;this.invalidate();this.courseSelections={};this.teacherSelections={};this.roomSelections={};this.dirtyMappings=false;this.error.set('');this.busy.set(true);this.api.inspectSectionImport(this.file,this.sheet,this.draftHeaderStartRow,this.draftHeaderRowCount).subscribe({next:r=>{this.applyInspection(r);this.sheetDetails=this.sheetDetails.map(x=>x.name===r.name?r:x);this.busy.set(false);if(advance)this.step.set(3);},error:e=>{this.error.set(errorText(e));this.busy.set(false);}});}
  weekMappings():any[]{return this.mappings.filter(m=>/^week\d+$/.test(m.field)&&m.columnIndex!=null).sort((a,b)=>Number(a.field.slice(4))-Number(b.field.slice(4)));}
  singleMappings():any[]{return this.mappings.filter(m=>m.field!=='ordinal'&&!/^week\d+$/.test(m.field)&&(m.field!=='weeks'||(!this.weekMappings().length&&this.inspection()?.format!=='HUIT')));}
  columnLabel(index:number|null):string{const column=this.inspection()?.columns?.find((c:any)=>c.index===index);return column?`${column.header} — ${column.letter} — ${column.sample||'(trống)'}`:'Không ánh xạ';}
  mappingChoices(mapping:any):ImportColumnChoice[]{return (this.inspection()?.columns??[]).map((c:any)=>({index:c.index,label:this.columnLabel(c.index),usedBy:this.mappings.filter(m=>m!==mapping&&m.field!=='ordinal'&&m.columnIndex===c.index).map(m=>m.label).join(', ')}));}
  mapColumn(mapping:any,index:number|null):void{if(index!==null&&this.mappingChoices(mapping).some(x=>x.index===index&&x.usedBy))return;mapping.columnIndex=index;this.invalidate();this.dirtyMappings=true;}
  weekMappingSummary():string{const weeks=this.weekMappings(),columns=this.inspection()?.columns??[];if(!weeks.length)return 'Chưa nhận diện nhóm cột tuần';const letter=(m:any)=>columns.find((c:any)=>c.index===m.columnIndex)?.letter??'';return `Đã ánh xạ ${weeks[0].label}–${weeks[weeks.length-1].label}, cột ${letter(weeks[0])}–${letter(weeks[weeks.length-1])}`;}
  quickRows():any[]{return (this.inspection()?.sampleRows??[]).slice(0,5).map((row:any)=>{const cell=(field:string)=>{const index=this.mappings.find(m=>m.field===field)?.columnIndex;return index==null?'':row.cells[index]??'';};const weeks=this.weekMappings();const selected=weeks.filter(m=>/^(x|1|true|✓|✔|☑|√)$/i.test(String(row.cells[m.columnIndex]??'').trim())).map(m=>m.label);return {row:row.row,source:cell('source'),course:cell('courseName')||cell('courseCode'),activity:cell('activity'),day:cell('day'),periods:`${cell('startPeriod')}–${cell('endPeriod')}`,room:cell('room'),from:cell('from'),to:cell('to'),weeks:weeks.length?(selected.join(',')||'Không có tuần được chọn'):(cell('weeks')||'Tất cả tuần')};});}
  mappingComplete():boolean{const assigned=(key:string)=>this.mappings.some(m=>m.field===key&&m.columnIndex!=null);const indexes=this.mappings.filter(m=>m.field!=='ordinal'&&m.columnIndex!=null).map(m=>m.columnIndex);return this.mappings.length>0&&new Set(indexes).size===indexes.length&&this.mappings.every(m=>!m.required||m.columnIndex!=null)&&(assigned('courseCode')||assigned('courseName'))&&(assigned('teacherCode')||assigned('teacher'));}
  showPreview(): void {
    if(!this.file||this.busy())return;this.invalidate();this.busy.set(true);this.error.set('');
    const settings={headerRow:this.headerStartRow,headerRowCount:this.headerRowCount,columnMappings:Object.fromEntries(this.mappings.map(m=>[m.field,m.columnIndex??-1])),courseSelections:this.courseSelections,teacherSelections:this.teacherSelections,roomSelections:this.roomSelections};
    this.api.previewSectionImport(this.file,this.sheet,this.termId!,this.policyId!,this.phase,settings).subscribe({next:r=>{this.preview.set(r);this.dirtyMappings=false;this.query='';this.statusFilter='ALL';this.busy.set(false);this.step.set(4);},error:e=>{this.error.set(errorText(e));this.busy.set(false);}});
  }
  loadPage(page:number):void{if(!this.preview()||this.busy())return;this.busy.set(true);this.error.set('');this.api.sectionImportPage(this.preview().token,page,this.pageSize,this.query,this.statusFilter).subscribe({next:r=>{this.preview.set(r);this.busy.set(false);},error:e=>{this.error.set(errorText(e));this.busy.set(false);}});}
  selectCourse(source:string,id:number|null):void{if(id)this.courseSelections[source]=id;else delete this.courseSelections[source];this.dirtyMappings=true;this.confirmed=false;}
  selectTeacher(row:number,id:number|null):void{if(id)this.teacherSelections[row]=id;else delete this.teacherSelections[row];this.dirtyMappings=true;this.confirmed=false;}
  selectRoom(row:number,id:number|null):void{if(id)this.roomSelections[row]=id;else delete this.roomSelections[row];this.dirtyMappings=true;this.confirmed=false;}
  confirmImport(): void {
    if(this.step()!==5||!this.preview()||!this.confirmed||this.busy()||this.dirtyMappings||this.preview().errorRows>0)return;this.busy.set(true);this.error.set('');
    this.api.confirmSectionImport(this.preview().token).subscribe({next:r=>{this.result.set(r);this.resultPage=1;this.busy.set(false);this.step.set(6);this.imported.emit();},error:e=>{this.error.set(errorText(e));this.busy.set(false);}});
  }
  back():void{if(this.busy())return;this.confirmed=false;this.step.set(Math.max(1,this.step()-1));}
  downloadTemplate():void{this.api.sectionImportTemplate().subscribe({next:b=>this.saveBlob(b,'mau-nhap-lop-hoc-phan.xlsx'),error:e=>this.error.set(errorText(e))});}
  downloadResult():void{if(!this.preview())return;this.api.sectionImportResultFile(this.preview().token).subscribe({next:b=>this.saveBlob(b,'ket-qua-nhap-lop-hoc-phan.csv'),error:e=>this.error.set(errorText(e))});}
  downloadValidation():void{if(!this.preview())return;this.api.sectionImportValidationFile(this.preview().token).subscribe({next:b=>this.saveBlob(b,'bao-cao-kiem-tra-lop-hoc-phan.csv'),error:e=>this.error.set(errorText(e))});}
  private saveBlob(blob:Blob,name:string):void{const url=URL.createObjectURL(blob);const link=document.createElement('a');link.href=url;link.download=name;link.click();URL.revokeObjectURL(url);}
  resultClasses():any[]{return this.result()?.classes.slice((this.resultPage-1)*100,this.resultPage*100)??[];}
  resultErrors():any[]{return this.result()?.errors.slice((this.resultPage-1)*100,this.resultPage*100)??[];}
  status(value:string):string{return ({VALID:'Hợp lệ',WARNING:'Cảnh báo',ERROR:'Lỗi',DUPLICATE:'Trùng dữ liệu'} as Record<string,string>)[value]??value;}
  date(value:string|undefined):string{return value&&!value.startsWith('0001-')?value.split('-').reverse().join('/'):'Chưa xác định';}
}
