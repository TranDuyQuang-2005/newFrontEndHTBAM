import { Component, ElementRef, HostListener, Input, OnChanges, OnDestroy, ViewChild, forwardRef, booleanAttribute } from '@angular/core';
import { AbstractControl, ControlValueAccessor, NG_VALIDATORS, NG_VALUE_ACCESSOR, Validator } from '@angular/forms';
import { formatDateOnly, parseVietnameseDate } from './date-only';

export { parseVietnameseDate } from './date-only';

let nextDateId = 0;

@Component({
  selector: 'app-date-input', standalone: true,
  providers: [
    { provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => DateInputComponent), multi: true },
    { provide: NG_VALIDATORS, useExisting: forwardRef(() => DateInputComponent), multi: true },
  ],
  styles: [`:host{display:block;min-width:0}.date-field{position:relative;display:flex}.date-field input{width:100%;padding-right:44px}.date-toggle{position:absolute;right:3px;top:3px;bottom:3px;min-width:36px;padding:4px;background:transparent;color:#475569;border:0}.date-calendar{position:fixed;z-index:3000;width:286px;max-width:calc(100vw - 16px);padding:12px;background:white;border:1px solid #cbd5e1;border-radius:12px;box-shadow:0 12px 40px #0f172a33}.calendar-heading{display:flex;align-items:center;justify-content:space-between;margin-bottom:8px}.calendar-heading button{padding:4px 10px;min-height:32px}.calendar-grid{display:grid;grid-template-columns:repeat(7,1fr);gap:2px}.calendar-grid span{text-align:center;font-size:12px;padding:4px}.calendar-grid button{padding:4px;min-height:30px;background:white;color:#334155;border:0}.calendar-grid button:hover,.calendar-grid button:focus-visible{background:#e0e7ff}.calendar-grid button.selected{background:#4f46e5;color:white}.calendar-grid button:disabled{opacity:.3}.date-error{display:block;color:#b91c1c;font-size:12px;margin-top:4px}`],
  template: `<div class="date-field"><input #textInput type="text" [placeholder]="placeholder" [attr.aria-label]="label" [attr.aria-invalid]="showError ? 'true' : null" [attr.aria-describedby]="showError ? id + '-error' : null" [attr.required]="required ? '' : null" [value]="display" [disabled]="disabled" (input)="input($event)" (blur)="blur()" />
    <button #toggle type="button" class="date-toggle" [disabled]="disabled" [attr.aria-label]="'Chọn ' + label" [attr.aria-expanded]="open" [attr.aria-controls]="id" (click)="toggleCalendar()">▦</button></div>
    @if(showError){<small class="date-error" [id]="id + '-error'" role="alert">{{validationMessage}}</small>}
    @if(open){<div class="date-calendar" [id]="id" role="dialog" [attr.aria-label]="'Lịch chọn ' + label" [style.top.px]="top" [style.left.px]="left" (keydown)="calendarKey($event)">
      <div class="calendar-heading"><button type="button" aria-label="Tháng trước" (click)="moveMonth(-1)">‹</button><strong>Tháng {{month + 1}} / {{year}}</strong><button type="button" aria-label="Tháng sau" (click)="moveMonth(1)">›</button></div>
      <div class="calendar-grid">@for(day of weekdays;track day){<span>{{day}}</span>}
        @for(day of days;track $index){@if(day){<button type="button" [attr.data-day]="day" [attr.aria-label]="dayLabel(day)" [attr.aria-pressed]="iso(day) === value" [class.selected]="iso(day) === value" [disabled]="!allowed(iso(day))" (click)="choose(day)">{{day}}</button>}@else{<span></span>}}</div></div>}`,
})
export class DateInputComponent implements ControlValueAccessor, Validator, OnChanges, OnDestroy {
  @Input() label = 'Ngày (dd/MM/yyyy)';
  @Input() placeholder = 'dd/MM/yyyy';
  @Input() min: string | null = null;
  @Input() max: string | null = null;
  @Input({transform: booleanAttribute}) required = false;
  @Input({transform: booleanAttribute}) disabled = false;
  @Input() errorMessage = '';
  @ViewChild('textInput') textInput?: ElementRef<HTMLInputElement>;
  @ViewChild('toggle') toggle?: ElementRef<HTMLButtonElement>;
  readonly id = `date-calendar-${++nextDateId}`;
  readonly weekdays = ['T2','T3','T4','T5','T6','T7','CN'];
  display = ''; value = ''; touched = false; open = false;
  year = new Date().getFullYear(); month = new Date().getMonth(); top = 0; left = 0;
  private change: (value: string) => void = () => {};
  private touch: () => void = () => {};
  private validatorChanged: () => void = () => {};
  private readonly scrollListener = (event: Event) => this.scrolled(event);

  constructor(private host: ElementRef<HTMLElement>) { this.host?.nativeElement.ownerDocument.addEventListener('scroll', this.scrollListener, true); }
  ngOnDestroy(): void { this.host?.nativeElement.ownerDocument.removeEventListener('scroll', this.scrollListener, true); }
  get invalid(): boolean { return !!this.display && !parseVietnameseDate(this.display); }
  get validationMessage(): string {
    if (this.invalid) return 'Ngày phải có dạng dd/MM/yyyy và là ngày có thực.';
    if (!this.value && this.required) return 'Vui lòng nhập hoặc chọn ngày.';
    if (this.value && this.min && this.value < this.min.slice(0,10)) return this.errorMessage || `Ngày phải lớn hơn hoặc bằng ${formatDateOnly(this.min)}.`;
    if (this.value && this.max && this.value > this.max.slice(0,10)) return this.errorMessage || `Ngày phải nhỏ hơn hoặc bằng ${formatDateOnly(this.max)}.`;
    return '';
  }
  get showError(): boolean { return (this.touched || !!this.value) && !!this.validationMessage; }
  get days(): number[] { const offset = (new Date(this.year,this.month,1).getDay()+6)%7; return [...Array(offset).fill(0), ...Array.from({length:new Date(this.year,this.month+1,0).getDate()},(_,i)=>i+1)]; }
  iso(day: number): string { return `${this.year}-${String(this.month+1).padStart(2,'0')}-${String(day).padStart(2,'0')}`; }
  dayLabel(day: number): string { return formatDateOnly(this.iso(day)); }
  allowed(iso: string): boolean { return (!this.min || iso >= this.min.slice(0,10)) && (!this.max || iso <= this.max.slice(0,10)); }
  ngOnChanges(): void { if(this.disabled)this.close(false); this.validatorChanged(); }
  writeValue(value: string | null): void { this.value = value?.slice(0,10) ?? ''; this.display = formatDateOnly(value); }
  registerOnChange(fn: (value: string) => void): void { this.change = fn; }
  registerOnTouched(fn: () => void): void { this.touch = fn; }
  registerOnValidatorChange(fn: () => void): void { this.validatorChanged = fn; }
  setDisabledState(value: boolean): void { this.disabled = value; if(value)this.close(false); }
  input(event: Event): void { this.display = (event.target as HTMLInputElement).value; this.value = parseVietnameseDate(this.display) ?? ''; this.change(this.value); this.validatorChanged(); }
  blur(): void { this.touched = true; if(this.value)this.display = formatDateOnly(this.value); this.touch(); }
  validate(_: AbstractControl): object | null { return this.validationMessage ? { date: this.validationMessage } : null; }
  toggleCalendar(): void {
    if(this.open){this.close(true);return;}
    const date = this.value || this.min;
    if(date){this.year=Number(date.slice(0,4));this.month=Number(date.slice(5,7))-1;}
    const rect = this.host.nativeElement.getBoundingClientRect();
    const footer = this.host.nativeElement.closest('[role="dialog"]')?.querySelector('.modal-footer,.catalog-footer,.form-actions')?.getBoundingClientRect();
    const bottom = Math.min(innerHeight-8,footer?.top ?? innerHeight-8);
    const height=304;
    this.top = rect.bottom+height <= bottom ? rect.bottom+4 : Math.max(8,rect.top-height-4);
    this.left=Math.max(8,Math.min(rect.left,innerWidth-294));this.open=true;
    setTimeout(()=>this.host.nativeElement.querySelector<HTMLButtonElement>('.calendar-grid button.selected:not(:disabled),.calendar-grid button:not(:disabled)')?.focus());
  }
  moveMonth(delta: number): void { const date=new Date(this.year,this.month+delta,1);if(date.getFullYear()<1000||date.getFullYear()>9999)return;this.year=date.getFullYear();this.month=date.getMonth(); }
  choose(day: number): void { this.value=this.iso(day);this.display=formatDateOnly(this.value);this.change(this.value);this.blur();this.validatorChanged();this.close(false);this.textInput?.nativeElement.focus(); }
  close(focus: boolean): void { this.open=false;if(focus)this.toggle?.nativeElement.focus(); }
  calendarKey(event: KeyboardEvent): void {
    if(event.key==='Escape'){event.preventDefault();event.stopPropagation();this.close(true);return;}
    const target=event.target as HTMLElement;const day=Number(target.dataset['day']);
    const delta=({ArrowLeft:-1,ArrowRight:1,ArrowUp:-7,ArrowDown:7} as Record<string,number>)[event.key];
    if(day&&delta){event.preventDefault();this.host.nativeElement.querySelector<HTMLButtonElement>(`[data-day="${day+delta}"]`)?.focus();}
  }
  @HostListener('document:pointerdown',['$event']) outside(event: Event): void { if(this.open&&!this.host.nativeElement.contains(event.target as Node))this.close(false); }
  @HostListener('document:focusin',['$event']) outsideFocus(event: Event): void { if(this.open&&!this.host.nativeElement.contains(event.target as Node))this.close(false); }
  @HostListener('window:resize') resized(): void { this.close(false); }
  @HostListener('keydown.escape',['$event']) escape(event: KeyboardEvent): void { if(this.open)this.calendarKey(event); }
  @HostListener('document:submit',['$event']) submitted(event: Event): void { if(this.host.nativeElement.closest('form')===event.target){this.touched=true;this.touch();} }
  scrolled(event: Event): void { if(this.open&&!(event.target as HTMLElement).closest?.('.date-calendar'))this.close(false); }
}
