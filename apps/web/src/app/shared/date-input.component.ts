import { Component, Input, forwardRef } from '@angular/core';
import { AbstractControl, ControlValueAccessor, NG_VALIDATORS, NG_VALUE_ACCESSOR, Validator } from '@angular/forms';

export function parseVietnameseDate(value: string): string | null {
  const match = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(value);
  if (!match) return null;
  const [, day, month, year] = match;
  const date = new Date(Number(year), Number(month) - 1, Number(day));
  return date.getFullYear() === Number(year) && date.getMonth() === Number(month) - 1 && date.getDate() === Number(day) ? `${year}-${month}-${day}` : null;
}

@Component({
  selector: 'app-date-input', standalone: true,
  providers: [
    { provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => DateInputComponent), multi: true },
    { provide: NG_VALIDATORS, useExisting: forwardRef(() => DateInputComponent), multi: true },
  ],
  template: `<input type="text" inputmode="numeric" placeholder="dd/MM/yyyy" [attr.aria-label]="label" [value]="display" [disabled]="disabled" (input)="input($event)" (blur)="touch()" />
    @if (invalid) { <small class="error-text">Ngày phải đúng dd/MM/yyyy.</small> }`,
})
export class DateInputComponent implements ControlValueAccessor, Validator {
  @Input() label = 'Ngày (dd/MM/yyyy)';
  display = ''; disabled = false; invalid = false;
  private change: (value: string) => void = () => {};
  touch: () => void = () => {};
  writeValue(value: string | null): void { this.display = value ? value.slice(0, 10).split('-').reverse().join('/') : ''; this.invalid = false; }
  registerOnChange(fn: (value: string) => void): void { this.change = fn; }
  registerOnTouched(fn: () => void): void { this.touch = fn; }
  setDisabledState(value: boolean): void { this.disabled = value; }
  input(event: Event): void { this.display = (event.target as HTMLInputElement).value; const iso = parseVietnameseDate(this.display); this.invalid = !!this.display && !iso; this.change(iso ?? ''); }
  validate(_: AbstractControl): object | null { return this.invalid ? { date: true } : null; }
}
