import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { Session } from '../core/models';
import { PageTitleComponent, behaviorText, errorText, fmtDate, statusText } from '../shared/ui';

@Component({standalone:true,imports:[FormsModule,PageTitleComponent],changeDetection:ChangeDetectionStrategy.OnPush,template:`
  <app-page-title title="Lịch sử" subtitle="Tra cứu hành vi, chuyên cần và nhận diện theo buổi học." />
  <div class="filter-bar"><label>Buổi học<select [(ngModel)]="sessionId"><option [ngValue]="null">Chọn buổi học</option>@for(session of sessions();track session.id){<option [ngValue]="session.id">{{sessionLabel(session)}}</option>}</select></label><label>Loại dữ liệu<select [(ngModel)]="kind"><option value="behaviors">Hành vi</option><option value="attendance">Chuyên cần</option><option value="identities">Nhận diện</option></select></label><button type="button" [disabled]="!sessionId || loading()" (click)="load()">{{loading()?'Đang tải…':'Tải dữ liệu'}}</button></div>
  @if(error()){<div class="error-box">{{error()}}</div>}
  <section class="card table-card"><div class="table-wrap"><table><thead><tr><th>Đối tượng</th><th>Hành vi / Trạng thái</th><th>Thời gian</th></tr></thead><tbody>
    @for(item of data();track item.id){<tr><td>{{subject(item)}}</td><td><span class="badge blue">{{state(item)}}</span></td><td>{{date(time(item))}}</td></tr>}
    @empty{<tr><td colspan="3" class="empty">{{emptyMessage()}}</td></tr>}
  </tbody></table></div></section>
`})
export class HistoryComponent implements OnInit{
  private readonly api=inject(ApiService);sessionId:number|null=null;kind:'behaviors'|'attendance'|'identities'='behaviors';readonly sessions=signal<Session[]>([]);readonly data=signal<any[]>([]);readonly error=signal('');readonly loading=signal(false);readonly date=fmtDate;
  ngOnInit(){this.api.sessions().subscribe({next:items=>this.sessions.set(items),error:error=>this.error.set(errorText(error))})}
  load(){if(!this.sessionId)return;this.loading.set(true);this.error.set('');this.api.historySession(this.sessionId,this.kind).subscribe({next:(items)=>{this.data.set(items);this.loading.set(false)},error:(error)=>{this.error.set(errorText(error));this.loading.set(false)}})}
  subject(item:any){return item.studentCode||item.fullName||(item.studentId?'Sinh viên đã xác định':item.stableId||item.stableIdentityId?'Đối tượng đang theo dõi':'Chưa xác định')}
  state(item:any){const value=item.label||item.status||item.identityStatus||item.state;return item.label?behaviorText(value):statusText(value)}
  time(item:any){return item.startedAt||item.lastSeenAt||item.createdAt||item.updatedAt}
  sessionLabel(session:Session){return`${session.classSection||session.course||'Buổi học'} · ${fmtDate(session.scheduledStart)}`}
  emptyMessage(){if(!this.sessionId)return'Chọn buổi học và loại dữ liệu để tra cứu.';return this.kind==='behaviors'?'Không ghi nhận hành vi trong buổi học này.':this.kind==='attendance'?'Không có bản ghi chuyên cần cho buổi học này.':'Không có dữ liệu nhận diện trong buổi học này.'}
}
