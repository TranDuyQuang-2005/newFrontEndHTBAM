import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { AttendancePolicy } from '../core/models';
import { ModalComponent, PageTitleComponent, RowActionMenuComponent, apiFieldErrors, errorText } from '../shared/ui';

@Component({
  standalone: true,
  imports: [FormsModule, ModalComponent, PageTitleComponent, RowActionMenuComponent],
  template: `
    <app-page-title title="Chuyên cần" subtitle="Theo dõi chính sách tính trạng thái có mặt, một phần và vắng mặt.">
      @if (canManage()) { <button type="button" (click)="startAdd()">+ Thêm chính sách</button> }
    </app-page-title>
    @if (error()) { <div class="error-box">{{ error() }}</div> }
    @if (message()) { <div class="success-box">{{ message() }}</div> }

    <div class="stats stats-three">
      <section class="stat-card"><span>Chính sách</span><strong>{{ items().length }}</strong><small>Đang cấu hình</small></section>
      <section class="stat-card"><span>Ngưỡng có mặt</span><strong>{{ items()[0]?.presentThreshold ?? '—' }}</strong><small>Chính sách hiện hành</small></section>
      <section class="stat-card"><span>Ngưỡng một phần</span><strong>{{ items()[0]?.partialThreshold ?? '—' }}</strong><small>Chính sách hiện hành</small></section>
    </div>

    <section class="card table-card"><div class="card-header"><div><h3>Chính sách chuyên cần</h3><p>Cấu hình nghiệp vụ từ API</p></div></div><div class="table-wrap"><table><thead><tr><th>Mã</th><th>Tên</th><th>Ngưỡng có mặt</th><th>Ngưỡng một phần</th><th>Điểm có mặt / một phần / vắng</th><th>Phiên bản</th><th>Trạng thái</th>@if(canManage()){<th>Thao tác</th>}</tr></thead><tbody>
      @for (policy of items(); track policy.id) {
        <tr><td class="cell-title">{{ policy.code }}</td><td>{{ policy.name }}</td><td>{{ policy.presentThreshold }}</td><td>{{ policy.partialThreshold }}</td><td>{{ policy.presentScore }} / {{ policy.partialScore }} / {{ policy.absentScore }}</td><td><span class="badge blue">{{ policy.version }}</span></td><td><span class="badge" [class.gray]="!policy.isActive">{{policy.isActive?'Hoạt động':'Ngừng hoạt động'}}</span></td>@if(canManage()){<td class="menu-cell"><app-row-action-menu><button type="button" (click)="startEdit(policy)">Chỉnh sửa</button>@if(policy.isActive){<button type="button" class="danger" (click)="askDeactivate(policy)">Ngừng hoạt động</button>}@else{<button type="button" (click)="reactivate(policy)">Kích hoạt lại</button>}</app-row-action-menu></td>}</tr>
      } @empty { <tr><td [attr.colspan]="canManage()?8:7" class="empty">Chưa có chính sách chuyên cần.</td></tr> }
    </tbody></table></div></section>

    @if (modalOpen()) {
      <app-modal [title]="editingId() ? 'Chỉnh sửa chính sách chuyên cần' : 'Thêm chính sách chuyên cần'" [busy]="saving()" size="lg" (close)="closeModal()">
        <form class="form-grid" (ngSubmit)="save()">
          @if (formError()) { <div class="error-box span2">{{ formError() }}</div> }
          <label>Mã nội bộ<input [value]="form.code || 'Tự động tạo khi lưu'" readonly />@if(fieldErrors()['code']){<small class="field-error">{{fieldErrors()['code']}}</small>}</label>
          <label><span>Tên <span class="required">*</span></span><input name="name" [(ngModel)]="form.name" /></label>
          <label><span>Ngưỡng có mặt <span class="required">*</span></span><input name="presentThreshold" type="number" min="0" max="1" step="0.01" [(ngModel)]="form.presentThreshold" /></label>
          <label><span>Ngưỡng một phần <span class="required">*</span></span><input name="partialThreshold" type="number" min="0" max="1" step="0.01" [(ngModel)]="form.partialThreshold" /></label>
          <label><span>Điểm có mặt <span class="required">*</span></span><input name="presentScore" type="number" step="0.1" [(ngModel)]="form.presentScore" /></label>
          <label><span>Điểm một phần <span class="required">*</span></span><input name="partialScore" type="number" step="0.1" [(ngModel)]="form.partialScore" /></label>
          <label><span>Điểm vắng <span class="required">*</span></span><input name="absentScore" type="number" step="0.1" [(ngModel)]="form.absentScore" /></label>
          <label><span>Phiên bản <span class="required">*</span></span><input name="version" [(ngModel)]="form.version" /></label>
          <label class="check"><input name="isActive" type="checkbox" [(ngModel)]="form.isActive" /> Hoạt động</label>
          <div class="modal-footer span2" style="margin:0 -20px -20px"><button type="button" class="secondary" [disabled]="saving()" (click)="closeModal()">Hủy</button><button type="submit" [disabled]="saving()">{{saving()?'Đang lưu…':'Lưu'}}</button></div>
        </form>
      </app-modal>
    }
    @if (pendingDeactivate()) {
      <app-modal title="Xác nhận ngừng chính sách" [busy]="saving()" size="sm" (close)="cancelDeactivate()">
        <p>Bạn có chắc muốn ngừng hoạt động chính sách <b>{{pendingDeactivate()!.code}}</b> không?</p>
        <div class="modal-footer" style="margin:20px -20px -20px"><button type="button" class="secondary" [disabled]="saving()" (click)="cancelDeactivate()">Hủy</button><button type="button" class="danger" [disabled]="saving()" (click)="confirmDeactivate()">{{saving()?'Đang xử lý…':'Xác nhận'}}</button></div>
      </app-modal>
    }
  `,
})
export class PoliciesComponent implements OnInit {
  private readonly api=inject(ApiService);private readonly auth=inject(AuthService);
  readonly items=signal<AttendancePolicy[]>([]);readonly error=signal('');readonly message=signal('');readonly formError=signal('');readonly fieldErrors=signal<Record<string,string>>({});readonly modalOpen=signal(false);readonly editingId=signal<number|null>(null);readonly saving=signal(false);readonly pendingDeactivate=signal<AttendancePolicy|null>(null);
  form=this.emptyForm();
  ngOnInit():void{this.load()}
  canManage():boolean{return this.auth.hasPermission('ATTENDANCE_POLICY_MANAGE')}
  load():void{this.api.policies().subscribe({next:items=>this.items.set(items),error:error=>this.error.set(errorText(error))})}
  startAdd():void{this.editingId.set(null);this.form=this.emptyForm();this.formError.set('');this.fieldErrors.set({});this.modalOpen.set(true)}
  startEdit(policy:AttendancePolicy):void{this.editingId.set(policy.id);this.form={code:policy.code,name:policy.name,presentThreshold:policy.presentThreshold,partialThreshold:policy.partialThreshold,presentScore:policy.presentScore,partialScore:policy.partialScore,absentScore:policy.absentScore,version:policy.version,isActive:policy.isActive};this.formError.set('');this.fieldErrors.set({});this.modalOpen.set(true)}
  closeModal():void{if(!this.saving())this.modalOpen.set(false)}
  save():void{if(!this.form.name.trim()||!this.form.version.trim()){this.formError.set('Vui lòng điền đầy đủ các trường bắt buộc.');return}if(this.form.presentThreshold<0||this.form.presentThreshold>1||this.form.partialThreshold<0||this.form.partialThreshold>1||this.form.presentThreshold<this.form.partialThreshold){this.formError.set('Ngưỡng chuyên cần phải từ 0 đến 1 và ngưỡng có mặt không nhỏ hơn ngưỡng một phần.');return}this.saving.set(true);this.formError.set('');this.fieldErrors.set({});this.api.savePolicy(this.editingId(),this.form).subscribe({next:()=>{this.saving.set(false);this.modalOpen.set(false);this.message.set('Đã lưu chính sách chuyên cần.');this.load()},error:error=>{this.saving.set(false);this.fieldErrors.set(apiFieldErrors(error));this.formError.set(errorText(error))}})}
  clearFieldError(field:string):void{this.fieldErrors.update(errors=>{if(!errors[field])return errors;const next={...errors};delete next[field];return next})}
  askDeactivate(policy:AttendancePolicy):void{this.pendingDeactivate.set(policy)}
  cancelDeactivate():void{if(!this.saving())this.pendingDeactivate.set(null)}
  confirmDeactivate():void{const policy=this.pendingDeactivate();if(!policy)return;this.saving.set(true);this.error.set('');this.api.deactivatePolicy(policy.id).subscribe({next:()=>{this.saving.set(false);this.pendingDeactivate.set(null);this.message.set('Đã ngừng hoạt động chính sách.');this.load()},error:error=>{this.saving.set(false);this.error.set(errorText(error))}})}
  reactivate(policy:AttendancePolicy):void{this.saving.set(true);this.error.set('');this.api.reactivatePolicy(policy.id).subscribe({next:()=>{this.saving.set(false);this.message.set('Đã kích hoạt lại chính sách.');this.load()},error:error=>{this.saving.set(false);this.error.set(errorText(error))}})}
  private emptyForm(){return{code:'',name:'',presentThreshold:.8,partialThreshold:.5,presentScore:10,partialScore:5,absentScore:0,version:'v1',isActive:true}}
}
