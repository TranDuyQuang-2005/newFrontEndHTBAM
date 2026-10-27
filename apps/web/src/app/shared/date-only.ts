export function parseVietnameseDate(value: string): string | null {
  const match = /^(\d{1,2})\/(\d{1,2})\/(\d{4})$/.exec(value.trim());
  if (!match) return null;
  const day = Number(match[1]), month = Number(match[2]), year = Number(match[3]);
  if (year < 1000 || year > 9999 || month < 1 || month > 12 || day < 1) return null;
  const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  if (day > [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][month - 1]) return null;
  return `${year}-${String(month).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
}

export function formatDateOnly(value: string | null | undefined): string {
  const iso = value?.slice(0, 10) ?? '';
  if (!/^\d{4}-\d{2}-\d{2}$/.test(iso)) return '';
  const display = iso.split('-').reverse().join('/');
  return parseVietnameseDate(display) === iso ? display : '';
}

export function normalizeAcademicYear(value: string): string { return value.trim().replace(/\s*[-‐‑‒–—−]\s*/g, '-'); }
export function validAcademicYear(value: string): boolean {
  return /^\d{4}-\d{4}$/.test(value) && Number(value.slice(0, 4)) >= 1000 && Number(value.slice(5)) === Number(value.slice(0, 4)) + 1;
}
export function normalizeSemester(value: string): string { const normalized = value.trim().toUpperCase(); return /^[12]$/.test(normalized) ? `HK${normalized}` : normalized; }
