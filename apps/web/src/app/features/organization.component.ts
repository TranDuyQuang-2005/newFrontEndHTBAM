import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { ModalComponent, PageTitleComponent, RowActionMenuComponent, errorText } from '../shared/ui';
import { SearchSelectComponent } from '../shared/search-select.component';

@Component({standalone:true,imports:[FormsModule,RouterLink,ModalComponent,PageTitleComponent,RowActionMenuComponent,SearchSelectComponent],template:`
  <app-page-title title="Cơ cấu & phân quyền" subtitle="Bổ nhiệm và thu hồi chức vụ từ hồ sơ giảng viên hiện có.">
    @if(auth.isAdmin()){<button type="button" (click)="beginFaculty()">Bổ nhiệm / chuyển giao Trưởng khoa</button>}
    @if(auth.hasPermission('DEPARTMENT_HEAD_ASSIGN')){<button type="button" [disabled]="!vacancies().length" (click)="beginDepartment()">Gán Trưởng bộ môn</button><a class="button-link secondary" routerLink="/class-sections">Cấp quyền theo lớp học phần</a>}
  </app-page-title>
  @if(error()){<div class="error-box" role="alert">{{error()}}</div>}
  @if(message()){<div class="success-box" role="status">{{message()}}</div>}
  @if(auth.hasPermission('DEPARTMENT_HEAD_ASSIGN')&&!vacancies().length){<div class="notice">Không còn bộ môn trống trong khoa. Sau khi thu hồi chức vụ, bộ môn sẽ xuất hiện lại để gán người khác.</div>}
  <section class="card table-card"><div class="table-wrap"><table><thead><tr><th>Giảng viên</th><th>Khoa / Bộ môn</th><th>Chức vụ còn hiệu lực</th><th>Trạng thái</th><th>Thao tác</th></tr></thead><tbody>
    @for(row of rows(); track row.id){<tr><td class="cell-title">{{row.teacherCode}}<small>{{row.fullName}}</small></td><td>{{row.faculty || 'Chưa thuộc khoa'}}<small>{{row.department || 'Chưa thuộc bộ môn'}}</small></td><td>{{row.positionLabel}}</td><td><span class="badge" [class.gray]="!row.isActive">{{row.isActive?'Hoạt động':'Ngừng hoạt động'}}</span></td><td class="menu-cell"><app-row-action-menu>
      <button type="button" (click)="showInfo(row)">Xem thông tin</button>
      @if(auth.isAdmin()&&row.isActive&&row.userId){<button type="button" (click)="beginFaculty(row)">Bổ nhiệm / chuyển giao Trưởng khoa</button>}
      @if(auth.hasPermission('DEPARTMENT_HEAD_ASSIGN')&&row.isActive&&vacant(row.departmentId)&&row.userId){<button type="button" (click)="beginDepartment(row)">Gán Trưởng bộ môn</button>}
      @for(a of row.assignments;track a.id){
        @if(auth.isAdmin()&&a.positionType==='FACULTY_HEAD'){<button type="button" (click)="editProfile(row,a)">Sửa thông tin Trưởng khoa</button>}
        @if((auth.isAdmin()&&a.positionType==='FACULTY_HEAD')||(auth.hasPermission('DEPARTMENT_HEAD_ASSIGN')&&a.positionType==='DEPARTMENT_HEAD')){<button type="button" class="danger" (click)="revoke(row,a)">Thu hồi {{a.positionType==='FACULTY_HEAD'?'Trưởng khoa':'Trưởng bộ môn'}}</button>}
      }
    </app-row-action-menu></td></tr>}@empty{<tr><td colspan="5" class="empty">Không có giảng viên trong phạm vi được phép.</td></tr>}
  </tbody></table></div></section>
  @if(action()){<app-modal [title]="title()" [busy]="saving()" (close)="closeAction()">
    @if(formError()){<div class="error-box">{{formError()}}</div>}
    @if(action().kind==='faculty'){
      <div class="stack"><label>Khoa<app-search-select resource="faculties" label="Khoa bổ nhiệm" [(ngModel)]="form.facultyId" (ngModelChange)="form.teacherId=null;form.confirmTransfer=false" /></label><label>Giảng viên hiện có<app-search-select resource="teachers" label="Giảng viên hiện có" [filters]="{facultyId:form.facultyId,eligibleHead:true}" [disabled]="!form.facultyId" [(ngModel)]="form.teacherId" /></label>
      @if(currentHead(form.facultyId)){<p class="notice">Trưởng khoa hiện tại: {{currentHead(form.facultyId).fullName}}.</p><label class="check"><input type="checkbox" [(ngModel)]="form.confirmTransfer" />Xác nhận chuyển giao chức vụ cho giảng viên đã chọn</label>}
      <label>Ghi chú<textarea [(ngModel)]="form.note"></textarea></label></div>
    }@else if(action().kind==='department'){
      <div class="stack"><label>Bộ môn chưa có trưởng bộ môn<app-search-select resource="departments" label="Bộ môn trống" [filters]="{vacantOnly:true}" [(ngModel)]="form.departmentId" (ngModelChange)="form.teacherId=null" /></label><label>Giảng viên thuộc bộ môn<app-search-select resource="teachers" label="Giảng viên thuộc bộ môn" [filters]="{departmentId:form.departmentId,eligibleHead:true}" [disabled]="!form.departmentId" [(ngModel)]="form.teacherId" /></label><label>Ghi chú<textarea [(ngModel)]="form.note"></textarea></label></div>
    }@else if(action().kind==='profile'){
      <div class="stack"><label>Họ tên<input [(ngModel)]="form.fullName" /></label><label>Email<input type="email" [(ngModel)]="form.email" /></label><label>Ghi chú<textarea [(ngModel)]="form.note"></textarea></label></div>
    }@else if(action().kind==='revoke'){
      <p>Thu hồi chức vụ của {{action().row.fullName}}? Tài khoản, hồ sơ giảng viên và dữ liệu giảng dạy được giữ lại.</p>
    }@else{
      <dl class="details-grid"><div><dt>Mã</dt><dd>{{action().row.teacherCode}}</dd></div><div><dt>Họ tên</dt><dd>{{action().row.fullName}}</dd></div><div><dt>Chức vụ</dt><dd>{{action().row.positionLabel}}</dd></div><div><dt>Email</dt><dd>{{action().row.email}}</dd></div><div><dt>Khoa / Bộ môn</dt><dd>{{action().row.faculty}} / {{action().row.department}}</dd></div></dl>
    }
    <div class="modal-footer" style="margin:20px -20px -20px"><button type="button" class="secondary" [disabled]="saving()" (click)="closeAction()">{{action().kind==='info'?'Đóng':'Hủy'}}</button>@if(action().kind!=='info'){<button type="button" [class.danger]="action().kind==='revoke'" [disabled]="saving()" (click)="submit()">{{saving()?'Đang lưu…':'Xác nhận'}}</button>}</div>
  </app-modal>}
`})
export class OrganizationComponent implements OnInit {
  private readonly api=inject(ApiService);readonly auth=inject(AuthService);readonly rows=signal<any[]>([]);readonly vacancies=signal<any[]>([]);readonly error=signal('');readonly message=signal('');readonly formError=signal('');readonly saving=signal(false);readonly action=signal<any>(null);form:any={};
  ngOnInit():void{this.load();}
  load():void{forkJoin({rows:this.api.managementAssignments(),vacancies:this.api.vacantDepartments()}).subscribe({next:r=>{this.rows.set(r.rows);this.vacancies.set(r.vacancies);},error:e=>this.error.set(errorText(e))});}
  vacant(id:number):boolean{return this.vacancies().some(x=>x.id===id);}
  currentHead(id:number|null):any{return this.rows().find(row=>row.assignments.some((a:any)=>a.positionType==='FACULTY_HEAD'&&a.facultyId===id));}
  beginFaculty(row?:any):void{this.form={facultyId:row?.facultyId??null,teacherId:row?.id??null,note:'',confirmTransfer:false};this.formError.set('');this.action.set({kind:'faculty'});}
  beginDepartment(row?:any):void{this.form={departmentId:row?.departmentId??null,teacherId:row?.id??null,note:''};this.formError.set('');this.action.set({kind:'department'});}
  showInfo(row:any):void{this.formError.set('');this.action.set({kind:'info',row});}
  editProfile(row:any,assignment:any):void{this.form={fullName:row.fullName,email:row.email,note:row.note??''};this.formError.set('');this.action.set({kind:'profile',row,assignment});}
  revoke(row:any,assignment:any):void{this.formError.set('');this.action.set({kind:'revoke',row,assignment});}
  title():string{return ({faculty:'Bổ nhiệm / chuyển giao Trưởng khoa',department:'Gán Trưởng bộ môn',revoke:'Thu hồi chức vụ',profile:'Thông tin Trưởng khoa',info:'Thông tin giảng viên'} as Record<string,string>)[this.action()?.kind]??'';}
  closeAction():void{if(!this.saving())this.action.set(null);}
  submit():void{const action=this.action();if(!action)return;this.saving.set(true);this.formError.set('');const operation=action.kind==='faculty'?this.api.transferFacultyHead(this.form):action.kind==='department'?this.api.assignDepartmentHead(this.form):action.kind==='profile'?this.api.updateHeadProfile(action.assignment.id,this.form):this.api.revokeManagementAssignment(action.assignment.id);operation.subscribe({next:()=>{this.saving.set(false);this.action.set(null);this.message.set('Đã cập nhật chức vụ/thông tin.');this.auth.ensureProfile(true).subscribe({next:()=>this.load(),error:e=>this.error.set(errorText(e))});},error:e=>{this.saving.set(false);this.formError.set(errorText(e));}});}
}
