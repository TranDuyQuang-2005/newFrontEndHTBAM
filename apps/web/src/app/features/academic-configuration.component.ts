import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { AcademicConfiguration } from '../core/models';
import { DateInputComponent } from '../shared/date-input.component';
import { ModalComponent, PageTitleComponent, RowActionMenuComponent, errorText } from '../shared/ui';

@Component({
  standalone:true, imports:[FormsModule,DateInputComponent,ModalComponent,PageTitleComponent,RowActionMenuComponent],
  template:`
    <app-page-title title="Cấu hình học vụ" subtitle="Ngày học kỳ, giai đoạn, phiên bản giờ học và ngoại lệ đã được xác nhận.">
      <button type="button" (click)="editTerm()">+ Cấu hình học kỳ</button><button type="button" class="secondary" (click)="newBell()">+ Phiên bản giờ học</button>
    </app-page-title>
    @if(error()){<div class="error-box" role="alert">{{error()}}</div>}
    @if(message()){<div class="success-box" role="status">{{message()}}</div>}
    <div class="notice">Ngày học kỳ và ranh giới giai đoạn phải có căn cứ. Excel dự kiến không tự xác nhận lịch chính thức. Bảng giờ HUIT có hiệu lực từ 12/01/2026.</div>
    <section class="card"><h3>Mốc bắt đầu Tuần 1</h3><p>T1 áp dụng cho năm học và học kỳ đã chọn; T2 cách T1 đúng 7 ngày. Dữ liệu DEMO là Mốc tuần mẫu.</p>
      <form class="form-grid" (ngSubmit)="saveWeekOne()"><label>Năm học / Học kỳ<select name="anchorTerm" [(ngModel)]="anchorTermId" (ngModelChange)="selectAnchor()"><option [ngValue]="null">Chọn học kỳ</option>@for(t of config()?.terms; track t.id){<option [ngValue]="t.id">{{t.academicYear}} · HK{{t.semester}}{{t.isDevelopment ? ' · Mốc tuần mẫu' : ''}}</option>}</select></label>
        <label>Mốc bắt đầu Tuần 1<app-date-input name="anchorDate" [(ngModel)]="anchor.weekOneMonday" /></label><label>Căn cứ<input name="anchorEvidence" [(ngModel)]="anchor.evidence" required /></label><label class="check"><input type="checkbox" name="anchorConfirmed" [(ngModel)]="anchor.confirmed" />Xác nhận mốc Tuần 1</label><div class="form-actions"><button type="submit" [disabled]="saving() || !anchorTermId || !anchor.weekOneMonday || !anchor.confirmed">Lưu mốc Tuần 1</button></div>
      </form>
    </section>
    <section class="card table-card"><div class="table-wrap"><table><thead><tr><th>Năm học</th><th>Học kỳ</th><th>Khoảng ngày</th><th>Giai đoạn</th><th>Mốc bắt đầu Tuần 1</th><th>Trạng thái</th><th>Thao tác</th></tr></thead><tbody>
      @for(term of config()?.terms; track term.id){<tr><td>{{term.academicYear}} @if(term.isDevelopment){<small>DEVELOPMENT / DEMO</small>}</td><td>{{term.semester}}</td><td>{{date(term.startsOn)}} – {{date(term.endsOn)}}</td><td>{{term.divisionPolicy === 'EXPLICIT' ? 'Ranh giới cấu hình' : 'Chia đều ngày'}}<small>{{term.phasesConfirmed ? 'Đã xác nhận' : 'Chưa xác nhận'}}</small></td><td>{{date(term.weekOneMonday)}}<small>{{term.weekMappingConfirmed ? 'Đã xác nhận' : 'Chưa xác nhận'}}</small></td><td><span class="badge" [class.warning]="!term.datesConfirmed">{{term.datesConfirmed ? 'Đã xác nhận ngày' : 'Thiếu cấu hình chính thức'}}</span></td><td class="menu-cell"><app-row-action-menu><button type="button" (click)="editTerm(term)">Xem / cấu hình</button></app-row-action-menu></td></tr>}
    </tbody></table></div></section>
    <section class="card"><h3>Phiên bản giờ học</h3><label>Chọn phiên bản<select [(ngModel)]="bellId"><option [ngValue]="null">Chọn phiên bản để xem 16 tiết</option>@for(version of config()?.bellVersions; track version.id){<option [ngValue]="version.id">{{version.code}} · Nhóm {{version.group}} · từ {{date(version.effectiveFrom)}}</option>}</select></label>
      @if(bellId){<div class="table-wrap" style="margin-top:16px"><table><thead><tr><th>Tiết</th><th>Ca</th><th>Bắt đầu</th><th>Kết thúc</th></tr></thead><tbody>@for(period of selectedPeriods(); track period.number){<tr><td>{{period.number}}</td><td>{{period.shift === 1 ? 'Sáng' : period.shift === 2 ? 'Chiều' : 'Tối'}}</td><td>{{period.startsAt.slice(0,5)}}</td><td>{{period.endsAt.slice(0,5)}}</td></tr>}</tbody></table></div>}
    </section>
    <section class="card"><h3>Ngoại lệ / ngày nghỉ</h3><form class="form-grid" (ngSubmit)="saveException()"><label>Học kỳ<select name="exceptionTerm" [(ngModel)]="exception.academicTermId" required><option [ngValue]="null">Chọn học kỳ</option>@for(term of config()?.terms; track term.id){<option [ngValue]="term.id">{{term.academicYear}} · HK{{term.semester}}</option>}</select></label><label>Ngày nghỉ<app-date-input name="exceptionDate" [(ngModel)]="exception.date" required /></label><label class="span2">Căn cứ / lý do<input name="exceptionReason" [(ngModel)]="exception.reason" required /></label><div class="form-actions"><button type="submit" [disabled]="saving()">Lưu ngoại lệ</button></div></form><p class="field-hint">Ngoại lệ áp dụng khi sinh/xem trước lịch; lớp đã có lịch cần xem và áp dụng ảnh hưởng trước khi đồng bộ buổi tương lai.</p>
      @for(item of config()?.exceptions; track item.id){<p>{{date(item.date)}} · {{item.reason}}</p>}
    </section>

    @if(termOpen()){<app-modal title="Cấu hình học kỳ và giai đoạn" size="lg" [busy]="saving()" (close)="termOpen.set(false)">
      @if(formError()){<div class="error-box">{{formError()}}</div>}
      <form class="stack" (ngSubmit)="saveTerm()"><div class="form-grid">
        <label>Năm học<input name="year" [(ngModel)]="term.academicYear" placeholder="2025-2026" required /></label><label>Học kỳ<select name="semester" [(ngModel)]="term.semester"><option [ngValue]="1">Học kỳ 1</option><option [ngValue]="2">Học kỳ 2</option></select></label>
        <label>Bắt đầu<app-date-input name="startsOn" [(ngModel)]="term.startsOn" /></label><label>Kết thúc<app-date-input name="endsOn" [(ngModel)]="term.endsOn" /></label>
        <label class="check span2"><input type="checkbox" name="datesConfirmed" [(ngModel)]="term.datesConfirmed" />Xác nhận đủ ngày học kỳ theo căn cứ bên dưới</label>
        <label>Chính sách giai đoạn<select name="divisionPolicy" [(ngModel)]="term.divisionPolicy"><option value="EXPLICIT">Nhập ranh giới chính thức</option><option value="EQUAL_DAYS">Chia đều ngày (cần xác nhận chính sách)</option></select></label><label>Mốc bắt đầu Tuần 1<app-date-input name="weekOne" [(ngModel)]="term.weekOneMonday" /></label>
        <label class="check span2"><input type="checkbox" name="weekConfirmed" [(ngModel)]="term.weekMappingConfirmed" />Mốc T1 đã được xác nhận cho học kỳ này</label>
        <label class="span2">Căn cứ cấu hình<textarea name="evidence" [(ngModel)]="term.evidence" placeholder="Tên văn bản, ngày áp dụng, nguồn xác nhận…"></textarea></label>
      </div>
      @if(term.divisionPolicy === 'EXPLICIT'){<h3>Hai phương án giai đoạn độc lập</h3>@for(phase of term.phases; track phase.credits + '-' + phase.number){<div class="form-grid"><label>Môn {{phase.credits}} TC · giai đoạn {{phase.number}}: bắt đầu<app-date-input [name]="'phase-start-' + phase.credits + '-' + phase.number" [(ngModel)]="phase.startsOn" /></label><label>Kết thúc<app-date-input [name]="'phase-end-' + phase.credits + '-' + phase.number" [(ngModel)]="phase.endsOn" /></label></div>}}
      @else{<div class="notice">Mỗi phương án chia 2/3 được tính độc lập theo số ngày, ngày dư phân bổ từ giai đoạn đầu. Chỉ dùng khi chính sách này đã được xác nhận; không tự coi đây là ranh giới chính thức.</div>}
      <label class="check"><input type="checkbox" name="phasesConfirmed" [(ngModel)]="term.phasesConfirmed" />Xác nhận chính sách và ranh giới giai đoạn theo căn cứ</label>
      <div class="modal-footer" style="margin:0 -20px -20px"><button type="button" class="secondary" [disabled]="saving()" (click)="termOpen.set(false)">Hủy</button><button type="submit" [disabled]="saving()">Lưu cấu hình</button></div></form>
    </app-modal>}
    @if(bellOpen()){<app-modal title="Thêm phiên bản giờ học có căn cứ" size="lg" [busy]="saving()" (close)="bellOpen.set(false)">
      @if(formError()){<div class="error-box">{{formError()}}</div>}
      <form class="stack" (ngSubmit)="saveBell()"><div class="form-grid"><label>Mã phiên bản<input name="bellCode" [(ngModel)]="bell.code" required /></label><label>Nhóm<select name="bellGroup" [(ngModel)]="bell.group"><option value="A">A · Lý thuyết / Zoom</option><option value="B">B · Máy tính / thực hành / TN</option></select></label><label>Hiệu lực từ<app-date-input name="effectiveFrom" [(ngModel)]="bell.effectiveFrom" required /></label><label>Hiệu lực đến (nếu có)<app-date-input name="effectiveTo" [(ngModel)]="bell.effectiveTo" /></label><label class="span2">Căn cứ bảng giờ<textarea name="bellEvidence" [(ngModel)]="bell.evidence" required></textarea></label></div>
      <div class="table-wrap"><table><thead><tr><th>Tiết</th><th>Bắt đầu</th><th>Kết thúc</th></tr></thead><tbody>@for(p of bell.periods; track p.number){<tr><td>{{p.number}}</td><td><input type="time" [name]="'bellStart'+p.number" [(ngModel)]="p.startsAt" required /></td><td><input type="time" [name]="'bellEnd'+p.number" [(ngModel)]="p.endsAt" required /></td></tr>}</tbody></table></div><label class="check"><input type="checkbox" name="bellConfirmed" [(ngModel)]="bell.confirmed" />Đã đối chiếu bảng giờ học, không dùng bảng giờ thi</label><div class="modal-footer" style="margin:0 -20px -20px"><button type="button" class="secondary" [disabled]="saving()" (click)="bellOpen.set(false)">Hủy</button><button type="submit" [disabled]="saving() || !bell.confirmed">Lưu phiên bản</button></div></form>
    </app-modal>}
  `,
})
export class AcademicConfigurationComponent implements OnInit {
  anchorTermId:number|null=null;anchor:any={weekOneMonday:'',confirmed:false,evidence:''};
  selectAnchor():void{const t=this.config()?.terms.find(t=>t.id===this.anchorTermId);this.anchor={weekOneMonday:t?.weekOneMonday??'',confirmed:t?.weekMappingConfirmed??false,evidence:t?.evidence??''};}
  saveWeekOne():void{if(!this.anchorTermId)return;this.saving.set(true);this.api.saveWeekOne(this.anchorTermId,this.anchor).subscribe({next:()=>{this.saving.set(false);this.message.set('Đã lưu Mốc bắt đầu Tuần 1.');this.load();},error:e=>{this.saving.set(false);this.error.set(errorText(e));}});}
  private readonly api=inject(ApiService);readonly config=signal<AcademicConfiguration|null>(null);readonly error=signal('');readonly message=signal('');readonly formError=signal('');readonly saving=signal(false);readonly termOpen=signal(false);readonly bellOpen=signal(false);
  term:any={};bell:any={};bellId:number|null=null;editingTermId:number|null=null;exception:any={academicTermId:null,date:'',reason:'',classSectionId:null};
  weekCalendar:any={academicTermId:null,name:'',weekOneMonday:'',evidence:'',confirmed:false};
  ngOnInit():void{this.load();}
  load():void{this.api.academicConfiguration().subscribe({next:r=>this.config.set(r),error:e=>this.error.set(errorText(e))});}
  editTerm(value?:any):void{this.formError.set('');this.editingTermId=value?.id??null;this.term={academicYear:'',semester:1,startsOn:'',endsOn:'',datesConfirmed:false,divisionPolicy:'EXPLICIT',phasesConfirmed:false,weekOneMonday:'',weekMappingConfirmed:false,evidence:'',...value,phases:[1,2].flatMap(credits=>Array.from({length:credits===1?3:2},(_,i)=>({credits,number:i+1,startsOn:'',endsOn:'',...this.config()?.phases.find(p=>p.academicTermId===value?.id&&p.credits===credits&&p.number===i+1)})))};this.termOpen.set(true);}
  saveTerm():void{this.saving.set(true);const body={...this.term,startsOn:this.term.startsOn||null,endsOn:this.term.endsOn||null,weekOneMonday:this.term.weekOneMonday||null,phases:this.term.phases.filter((p:any)=>p.startsOn&&p.endsOn)};this.api.saveAcademicTerm(this.editingTermId,body).subscribe({next:()=>{this.saving.set(false);this.termOpen.set(false);this.load();},error:e=>{this.saving.set(false);this.formError.set(errorText(e));}});}
  newBell():void{this.formError.set('');this.bell={code:'',group:'A',effectiveFrom:'',effectiveTo:'',evidence:'',confirmed:false,periods:Array.from({length:16},(_,i)=>({number:i+1,shift:i<6?1:i<12?2:3,startsAt:'',endsAt:''}))};this.bellOpen.set(true);}
  saveBell():void{this.saving.set(true);this.api.saveBellVersion({...this.bell,effectiveTo:this.bell.effectiveTo||null}).subscribe({next:()=>{this.saving.set(false);this.bellOpen.set(false);this.load();},error:e=>{this.saving.set(false);this.formError.set(errorText(e));}});}
  selectedPeriods():any[]{return this.config()?.periods.filter(p=>p.bellTimeVersionId===this.bellId)??[];}
  saveException():void{this.saving.set(true);this.api.saveScheduleException(this.exception).subscribe({next:()=>{this.saving.set(false);this.message.set('Đã lưu ngoại lệ; cần xem ảnh hưởng trước khi đồng bộ lịch đã có.');this.load();},error:e=>{this.saving.set(false);this.error.set(errorText(e));}});}
  saveWeekCalendar():void{this.saving.set(true);this.api.saveWeekCalendar(this.weekCalendar).subscribe({next:()=>{this.saving.set(false);this.load();},error:e=>{this.saving.set(false);this.error.set(errorText(e));}});}
  date(value:string|null):string{return value?value.split('-').reverse().join('/'):'Chưa xác nhận';}
}
