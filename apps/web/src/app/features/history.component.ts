import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { PageTitleComponent, behaviorText, errorText, fmtDate, statusText } from '../shared/ui';

@Component({standalone:true,imports:[FormsModule,PageTitleComponent],changeDetection:ChangeDetectionStrategy.OnPush,template:`
  <app-page-title title="Lịch sử" subtitle="Tra cứu hành vi, chuyên cần và nhận diện theo buổi học." />
  <div class="filter-bar"><label>Buổi học<input type="number" [(ngModel)]="sessionId" placeholder="Mã buổi học" /></label><label>Loại dữ liệu<select [(ngModel)]="kind"><option value="behaviors">Hành vi</option><option value="attendance">Chuyên cần</option><option value="identities">Nhận diện</option></select></label><button type="button" [disabled]="!sessionId || loading()" (click)="load()">{{loading()?'Đang tải…':'Tải dữ liệu'}}</button></div>
  @if(error()){<div class="error-box">{{error()}}</div>}
  <section class="card table-card"><div class="table-wrap"><table><thead><tr><th>ID</th><th>Sinh viên / Identity</th><th>Loại / Trạng thái</th><th>Giá trị</th><th>Thời gian</th></tr></thead><tbody>
    @for(item of data();track item.id){<tr><td>#{{item.id}}</td><td>{{subject(item)}}</td><td><span class="badge blue">{{state(item)}}</span></td><td>{{value(item)}}</td><td>{{date(time(item))}}</td></tr>}
    @empty{<tr><td colspan="5" class="empty">{{emptyMessage()}}</td></tr>}
  </tbody></table></div></section>
`})
export class HistoryComponent{
  private readonly api=inject(ApiService);sessionId:number|null=null;kind:'behaviors'|'attendance'|'identities'='behaviors';readonly data=signal<any[]>([]);readonly error=signal('');readonly loading=signal(false);readonly date=fmtDate;
  load(){if(!this.sessionId)return;this.loading.set(true);this.error.set('');this.api.historySession(this.sessionId,this.kind).subscribe({next:(items)=>{this.data.set(items);this.loading.set(false)},error:(error)=>{this.error.set(errorText(error));this.loading.set(false)}})}
  subject(item:any){return item.studentCode||item.fullName||(item.studentId?`Sinh viên #${item.studentId}`:item.stableId||item.stableIdentityId||'—')}
  state(item:any){const value=item.label||item.status||item.identityStatus||item.state;return item.label?behaviorText(value):statusText(value)}
  value(item:any){const n=item.probability??item.score??item.identityConfidence??item.observationQuality;return n==null?'—':`${Math.round(Number(n)*100)}%`}
  time(item:any){return item.startedAt||item.lastSeenAt||item.createdAt||item.updatedAt}
  emptyMessage(){if(!this.sessionId)return'Nhập mã buổi học và chọn loại dữ liệu để tra cứu.';return this.kind==='behaviors'?'0 sự kiện — không ghi nhận hành vi trong buổi học này.':this.kind==='attendance'?'Không có bản ghi chuyên cần cho buổi học này.':'Không ghi nhận danh tính từ pipeline trong buổi học này.'}
}
