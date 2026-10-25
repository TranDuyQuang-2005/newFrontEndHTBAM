import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { ApiService } from '../core/api.service';
import { FaceEnrollment, Student } from '../core/models';
import { BackButtonComponent, ModalComponent, errorText, statusText } from '../shared/ui';

@Component({
  standalone:true,
  imports:[FormsModule,BackButtonComponent,ModalComponent],
  changeDetection:ChangeDetectionStrategy.OnPush,
  template:`
  <app-back-button to="/students" label="Quay lại danh sách sinh viên" />
  <div class="page-title">
    <div>
      <h1>Đăng ký khuôn mặt sinh viên</h1>
      <p>Tạo mẫu khuôn mặt ArcFace để hệ thống nhận diện sinh viên trong buổi học. Đây là bước đăng ký mẫu, không huấn luyện lại mô hình.</p>
    </div>
    <div class="actions"><button type="button" [disabled]="busy()" (click)="start()">Tạo lần đăng ký mới</button></div>
  </div>
  @if(error()){<div class="error-box">{{error()}}</div>}
  @if(message()){<div class="success-box">{{message()}}</div>}

  <div class="dashboard-grid face-grid">
    <aside>
      <section class="card profile-card">
        @if(student()){
          <div class="avatar large">{{student()!.fullName[0]}}</div>
          <h2>{{student()!.fullName}}</h2>
          <p>{{student()!.studentCode}} · {{student()!.studentClass||'Chưa có lớp'}}</p>
          <span class="badge" [class.gray]="!student()!.isActive">{{student()!.isActive?'Hoạt động':'Ngừng hoạt động'}}</span>
        }
      </section>
      <section class="card">
        <h3>Các lần đăng ký</h3>
        @for(enrollment of list();track enrollment.id){
          <button type="button" class="list-button" [class.active]="selectedId()===enrollment.id" (click)="select(enrollment.id)">
            <span>#{{enrollment.id}} · {{statusLabel(enrollment.status)}}</span>
            <small style="margin-left:auto">{{enrollment.uploadedImageCount}} ảnh</small>
          </button>
        } @empty {<p class="empty">Chưa có lần đăng ký.</p>}
      </section>
      <section class="card">
        <h3>Hướng dẫn</h3>
        <ul style="padding-left:18px;color:#64748b;line-height:1.8;margin:0">
          <li>Tối thiểu 6 ảnh đạt chất lượng</li>
          <li>Bắt buộc có chính diện, nghiêng trái và nghiêng phải</li>
          <li>Mỗi ảnh chỉ có một khuôn mặt</li>
          <li>Ảnh rõ, đủ sáng, không che mặt</li>
        </ul>
      </section>
    </aside>

    <main>
      <section class="card">
        @if(detail()){
          <div class="card-header" style="margin:-20px -20px 18px">
            <div><h3>Lần đăng ký #{{detail().enrollment.id}}</h3><p>{{detail().images.length}} ảnh đã tải lên</p></div>
            <span class="badge blue">{{statusLabel(detail().enrollment.status)}}</span>
          </div>

          @if(detail().template){
            <div class="success-box">
              <b>Mẫu nhận diện đã sẵn sàng.</b>
              {{detail().template.modelName}} · {{detail().template.embeddingDimension}}D ·
              chất lượng {{percent(detail().template.qualityScore)}}
            </div>
          }
          @if(detail().enrollment.failureReason){
            <div class="error-box">{{detail().enrollment.failureReason}}</div>
          }
          @if(detail().enrollment.status==='PROCESSING'){
            <div class="notice">AI đang kiểm tra chất lượng ảnh và tạo template ArcFace. Trang sẽ tự cập nhật.</div>
          }

          @if(canUpload()){
            <div class="form-grid" style="margin-bottom:16px">
              <label>Góc chụp
                <select [(ngModel)]="pose">
                  <option value="FRONT">Chính diện</option>
                  <option value="LEFT">Nghiêng trái</option>
                  <option value="RIGHT">Nghiêng phải</option>
                  <option value="UP">Ngẩng lên</option>
                  <option value="DOWN">Cúi xuống</option>
                </select>
              </label>
              <label class="drop-zone" style="min-height:110px">
                <div><div class="upload-icon">⇧</div><strong>{{file?.name||'Chọn ảnh khuôn mặt'}}</strong><small>JPEG / PNG / WebP, tối đa 8 MB</small></div>
                <input type="file" accept="image/jpeg,image/png,image/webp" [disabled]="busy()" (change)="file=asFile($event)" />
              </label>
            </div>
            <div class="inline-actions" style="justify-content:flex-end">
              <button type="button" [disabled]="!file||busy()" (click)="upload()">Tải ảnh</button>
            </div>
          }

          <h3 style="margin-top:20px">Ảnh đã tải lên</h3>
          <div style="display:grid;grid-template-columns:repeat(auto-fill,minmax(170px,1fr));gap:12px;max-width:100%">
            @for(image of detail().images;track image.id){
              <article class="panel" style="overflow:hidden;min-width:0">
                <button type="button" class="secondary" style="width:100%;min-height:105px;border-radius:0" (click)="openPreview(image)">Xem ảnh</button>
                <div style="padding:10px;min-width:0">
                  <strong style="display:block;font-size:12px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">{{image.originalFileName}}</strong>
                  <small style="display:block;margin-top:4px;color:#64748b">{{poseLabel(image.capturePose)}} · {{statusLabel(image.qualityStatus)}}</small>
                  @if(image.qualityScore!=null){<small style="display:block;color:#64748b">Chất lượng: {{percent(image.qualityScore)}}</small>}
                  @if(image.qualityReason){<small class="field-error" style="display:block;margin-top:4px">{{image.qualityReason}}</small>}
                  @if(canUpload()){<button type="button" class="danger small" style="margin-top:8px" [disabled]="busy()" (click)="removeImage(image)">Xóa</button>}
                </div>
              </article>
            } @empty {
              <div class="empty-state" style="grid-column:1/-1"><div><strong>Chưa có ảnh</strong><span>Chọn ảnh để bắt đầu đăng ký.</span></div></div>
            }
          </div>

          <div class="form-actions" style="margin-top:20px">
            @if(canUpload()){<button type="button" [disabled]="busy()" (click)="submit()">Gửi kiểm tra AI</button>}
            @if(detail().enrollment.status==='PENDING_AI'){<button type="button" [disabled]="busy()" (click)="process()">Tạo mẫu ArcFace</button>}
            @if(['IN_PROGRESS','NEEDS_RETAKE','PENDING_AI'].includes(detail().enrollment.status)){
              <button type="button" class="secondary" [disabled]="busy()" (click)="cancel()">Hủy lần đăng ký</button>
            }
          </div>
        } @else {
          <div class="empty-state"><div><strong>Chọn hoặc tạo một lần đăng ký</strong><span>Chi tiết ảnh và trạng thái template sẽ xuất hiện tại đây.</span></div></div>
        }
      </section>
    </main>
  </div>

  @if(previewUrl()){
    <app-modal [title]="previewName() || 'Ảnh khuôn mặt'" size="md" (close)="closePreview()">
      <div style="display:grid;place-items:center;background:#0f172a;border-radius:12px;overflow:hidden">
        <img [src]="previewUrl()" [alt]="previewName()" style="max-width:100%;max-height:70vh;object-fit:contain" />
      </div>
    </app-modal>
  }
`})
export class FaceEnrollmentComponent implements OnInit{
  private readonly route=inject(ActivatedRoute);
  private readonly api=inject(ApiService);
  readonly studentId=Number(this.route.snapshot.paramMap.get('studentId'));
  readonly student=signal<Student|null>(null);
  readonly list=signal<FaceEnrollment[]>([]);
  readonly detail=signal<any>(null);
  readonly selectedId=signal<number|null>(null);
  readonly error=signal('');
  readonly message=signal('');
  readonly busy=signal(false);
  readonly previewUrl=signal('');
  readonly previewName=signal('');
  pose='FRONT';
  file:File|null=null;

  ngOnInit(){this.reload()}
  reload(){
    this.api.student(this.studentId).subscribe({next:v=>this.student.set(v),error:e=>this.error.set(errorText(e))});
    this.api.faceList(this.studentId).subscribe({next:items=>{this.list.set(items);if(items[0]&&!this.selectedId())this.select(items[0].id)},error:e=>this.error.set(errorText(e))})
  }
  select(id:number){this.selectedId.set(id);this.api.faceDetail(this.studentId,id).subscribe({next:v=>this.detail.set(v),error:e=>this.error.set(errorText(e))})}
  start(){this.busy.set(true);this.error.set('');this.message.set('');this.api.faceStart(this.studentId).subscribe({next:v=>{this.busy.set(false);this.reload();if(v?.id)this.select(v.id)},error:e=>{this.busy.set(false);this.error.set(errorText(e))}})}
  asFile(event:Event){return(event.target as HTMLInputElement).files?.[0]??null}
  canUpload(){const s=this.detail()?.enrollment.status;return s==='IN_PROGRESS'||s==='NEEDS_RETAKE'}
  upload(){const id=this.selectedId();if(!this.file||id===null)return;this.busy.set(true);this.api.faceUpload(this.studentId,id,this.file,this.pose).subscribe({next:()=>{this.file=null;this.busy.set(false);this.select(id)},error:e=>{this.busy.set(false);this.error.set(errorText(e))}})}
  submit(){const id=this.selectedId();if(id===null)return;this.busy.set(true);this.api.faceSubmit(this.studentId,id).subscribe({next:()=>{this.busy.set(false);this.select(id);this.reload()},error:e=>{this.busy.set(false);this.error.set(errorText(e))}})}
  process(){const id=this.selectedId();if(id===null)return;this.busy.set(true);this.error.set('');this.api.faceProcess(this.studentId,id).subscribe({next:()=>{this.busy.set(false);this.message.set('Đã gửi AI tạo mẫu ArcFace.');this.select(id);this.poll(id,0)},error:e=>{this.busy.set(false);this.error.set(errorText(e))}})}
  poll(id:number,attempt:number){if(attempt>=60)return;setTimeout(()=>this.api.faceDetail(this.studentId,id).subscribe({next:v=>{this.detail.set(v);const s=v?.enrollment?.status;if(s==='PROCESSING')this.poll(id,attempt+1);else this.reload()},error:()=>{}}),1500)}
  openPreview(image:any){const id=this.selectedId();if(id===null)return;this.api.facePreview(this.studentId,id,image.id).subscribe({next:v=>{this.previewName.set(image.originalFileName);this.previewUrl.set(v.url)},error:e=>this.error.set(errorText(e))})}
  closePreview(){this.previewUrl.set('');this.previewName.set('')}
  removeImage(image:any){const id=this.selectedId();if(id===null||!confirm(`Xóa ảnh ${image.originalFileName}?`))return;this.busy.set(true);this.api.faceDeleteImage(this.studentId,id,image.id).subscribe({next:()=>{this.busy.set(false);this.select(id)},error:e=>{this.busy.set(false);this.error.set(errorText(e))}})}
  cancel(){const id=this.selectedId();if(id===null||!confirm('Hủy lần đăng ký khuôn mặt này?'))return;this.busy.set(true);this.api.faceCancel(this.studentId,id).subscribe({next:()=>{this.busy.set(false);this.message.set('Đã hủy lần đăng ký.');this.select(id);this.reload()},error:e=>{this.busy.set(false);this.error.set(errorText(e))}})}
  statusLabel(status:string){return statusText(status)}
  percent(v:number){return `${Math.round(Number(v)*100)}%`}
  poseLabel(v:string){return ({FRONT:'Chính diện',LEFT:'Nghiêng trái',RIGHT:'Nghiêng phải',UP:'Ngẩng lên',DOWN:'Cúi xuống'} as Record<string,string>)[v]??v}
}
