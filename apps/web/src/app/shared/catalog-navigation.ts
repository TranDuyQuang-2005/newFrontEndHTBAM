export type CatalogTab = 'faculties' | 'departments' | 'student-classes' | 'courses' | 'class-sections' | 'teachers' | 'rooms' | 'cameras' | 'roster';
export interface CatalogNavigationAccess {
  isAdmin(): boolean;
  hasPermission(permission: string): boolean;
  user(): { roles?: string[] };
}
export const CATALOG_TABS: { key: CatalogTab; label: string; description?:string }[] = [
  { key: 'faculties', label: 'Khoa', description:'Quản lý danh sách khoa và khởi tạo cơ cấu khoa.' },
  { key: 'departments', label: 'Bộ môn', description:'Quản lý danh sách bộ môn trực thuộc khoa.' },
  { key: 'student-classes', label: 'Lớp sinh hoạt', description:'Quản lý các lớp sinh hoạt trong phạm vi khoa.' },
  { key: 'courses', label: 'Môn học', description:'Quản lý môn học và số tín chỉ theo từng bộ môn.' },
  { key: 'class-sections', label: 'Lớp học phần', description:'Lịch học, danh sách sinh viên và quyền theo từng lớp.' },
  { key: 'teachers', label: 'Giảng viên', description:'Quản lý hồ sơ giảng viên trong khoa.' },
  { key: 'rooms', label: 'Phòng học', description:'Quản lý phòng học, loại phòng và sức chứa.' },
  { key: 'cameras', label: 'Camera', description:'Quản lý camera và kết nối phòng học.' },
];

export function visibleCatalogTabs(auth: CatalogNavigationAccess): typeof CATALOG_TABS {
  return CATALOG_TABS.filter(tab => {
    if (tab.key === 'class-sections') return auth.hasPermission('CLASS_SECTION_VIEW');
    if (!auth.hasPermission('CATALOG_VIEW')) return false;
    if (auth.isAdmin()) return ['faculties', 'rooms', 'cameras'].includes(tab.key);
    if (auth.user().roles?.includes('TECH_AI') && !auth.hasPermission('DEPARTMENT_DATA_MANAGE')) return ['rooms', 'cameras'].includes(tab.key);
    return true;
  });
}

export function resolveCatalogTab(tabs: typeof CATALOG_TABS, requested: string | null): CatalogTab {
  return tabs.find(tab => tab.key === requested)?.key ?? tabs[0]?.key ?? 'faculties';
}

export function catalogBreadcrumb(url: string): string | null {
  const [path, search = ''] = url.split('#')[0].split('?');
  if (path.startsWith('/class-sections') || path === '/management' && new URLSearchParams(search).get('tab') === 'class-sections')
    return 'Danh mục dữ liệu / Lớp học phần';
  return path === '/management' ? 'Danh mục dữ liệu' : null;
}
