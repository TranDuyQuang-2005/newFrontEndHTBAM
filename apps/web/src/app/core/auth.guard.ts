import { inject } from '@angular/core';
import {
  CanActivateFn,
  Router,
} from '@angular/router';
import { catchError, map, of } from 'rxjs';
import { AuthService } from './auth.service';

export const authGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (!auth.isLoggedIn()) {
    return router.createUrlTree(['/login']);
  }

  return auth.ensureProfile().pipe(
    map(() => true),
    catchError((error) => {
      if (Number(error?.status) === 401) {
        auth.clear(false);
      }
      return of(router.createUrlTree(['/login']));
    }),
  );
};

export const guestGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (!auth.isLoggedIn()) {
    return true;
  }

  return auth.ensureProfile().pipe(
    map(() => router.createUrlTree(['/'])),
    catchError((error) => {
      if (Number(error?.status) === 401) {
        auth.clear(false);
      }
      return of(true);
    }),
  );
};

export const adminGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (!auth.isLoggedIn()) {
    return router.createUrlTree(['/login']);
  }

  return auth.ensureProfile().pipe(
    map(() =>
      auth.isAdmin()
        ? true
        : router.createUrlTree(['/']),
    ),
    catchError((error) => {
      if (Number(error?.status) === 401) {
        auth.clear(false);
      }
      return of(router.createUrlTree(['/login']));
    }),
  );
};

export const academicGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (!auth.isLoggedIn()) {
    return router.createUrlTree(['/login']);
  }

  return auth.ensureProfile().pipe(
    map(() =>
      auth.isAdmin()
        ? router.createUrlTree([
            '/admin/users',
          ])
        : true,
    ),
    catchError((error) => {
      if (Number(error?.status) === 401) {
        auth.clear(false);
      }
      return of(
        router.createUrlTree([
          '/login',
        ]),
      );
    }),
  );
};

export const organizationGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (!auth.isLoggedIn()) {
    return router.createUrlTree(['/login']);
  }

  return auth.ensureProfile().pipe(
    map(() => {
      return auth.canManageStructure()
        ? true
        : router.createUrlTree(['/']);
    }),
    catchError((error) => {
      if (Number(error?.status) === 401) {
        auth.clear(false);
      }
      return of(
        router.createUrlTree([
          '/login',
        ]),
      );
    }),
  );
};

function permissionGuard(permission: string): CanActivateFn {
  return () => {
    const auth = inject(AuthService);
    const router = inject(Router);
    if (!auth.isLoggedIn()) return router.createUrlTree(['/login']);
    return auth.ensureProfile(true).pipe(
      map(() => auth.hasPermission(permission) ? true : router.createUrlTree(auth.isAdmin() ? ['/admin/users'] : auth.hasPermission('CLASS_SECTION_VIEW') ? ['/class-sections'] : ['/system'])),
      catchError(() => of(router.createUrlTree(['/login']))),
    );
  };
}
export const studentManagementGuard = permissionGuard('STUDENT_GLOBAL_VIEW');
export const academicConfigurationGuard = permissionGuard('ACADEMIC_CONFIGURE');
export const reportGuard = permissionGuard('REPORT_VIEW');
export const catalogGuard: CanActivateFn = route => {
  const auth = inject(AuthService); const router = inject(Router);
  if (!auth.isLoggedIn()) return router.createUrlTree(['/login']);
  return auth.ensureProfile(true).pipe(
    map(() => {
      const requested = route.queryParamMap.get('tab');
      if (requested === 'class-sections') return auth.hasPermission('CLASS_SECTION_VIEW') ? true : router.createUrlTree(auth.hasPermission('CATALOG_VIEW') ? ['/management'] : ['/system']);
      if (auth.hasPermission('CATALOG_VIEW')) return true;
      return auth.hasPermission('CLASS_SECTION_VIEW') ? router.createUrlTree(['/management'], { queryParams: { tab: 'class-sections' } }) : router.createUrlTree(['/system']);
    }),
    catchError(() => of(router.createUrlTree(['/login']))),
  );
};

export const classSectionsGuard: CanActivateFn = route => {
  const auth = inject(AuthService); const router = inject(Router);
  if (!auth.isLoggedIn()) return router.createUrlTree(['/login']);
  return auth.ensureProfile(true).pipe(
    map(() => auth.hasPermission('CLASS_SECTION_VIEW')
      ? router.createUrlTree(['/management'], { queryParams: { ...route.queryParams, tab: 'class-sections' }, fragment: route.fragment ?? undefined })
      : router.createUrlTree(auth.hasPermission('CATALOG_VIEW') ? ['/management'] : ['/system'])),
    catchError(() => of(router.createUrlTree(['/login']))),
  );
};
