import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import {
  Observable,
  finalize,
  of,
  shareReplay,
  switchMap,
  tap,
  throwError,
} from 'rxjs';
import { LoginResponse, UserInfo } from './models';

@Injectable({
  providedIn: 'root',
})
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  private readonly tokenKey = 'htbam_token';
  private readonly userKey = 'htbam_user';

  private readonly tokenState = signal<string | null>(
    this.readInitialToken(),
  );

  private profileRequest?: Observable<UserInfo>;

  readonly user = signal<Partial<UserInfo>>({});
  readonly profileLoaded = signal(false);
  readonly profileLoading = signal(false);

  readonly isLoggedIn = computed(
    () => !!this.tokenState(),
  );

  readonly isAdmin = computed(
    () => this.user().roles?.includes('ADMIN') ?? false,
  );

  readonly canAssignSubstitute = computed(
    () => this.user().permissions?.includes('SUBSTITUTE_ASSIGN') ?? false,
  );

  readonly canManageStructure = computed(
    () =>
      this.profileLoaded() &&
      (this.hasPermission('FACULTY_HEAD_ASSIGN') || (this.user().managementAssignments?.length ?? 0) > 0),
  );

  readonly positionLabel = computed(() => this.user().positionLabel || (this.isAdmin() ? 'Quản trị viên' : 'Người dùng'));

  readonly canEditStudents = computed(
    () => this.user().permissions?.includes('STUDENT_EDIT_SCOPE') ?? false,
  );

  readonly canCreateStudents = computed(
    () => this.user().permissions?.includes('STUDENT_CREATE') ?? false,
  );

  readonly canEnrollFaces = computed(
    () => this.hasPermission('FACE_ENROLL_SCOPE'),
  );

  token(): string | null {
    return this.tokenState();
  }

  login(
    userName: string,
    password: string,
  ): Observable<UserInfo> {
    return this.http
      .post<LoginResponse>('/api/auth/login', {
        userName,
        password,
      })
      .pipe(
        tap((response) => {
          sessionStorage.setItem(
            this.tokenKey,
            response.accessToken,
          );

          this.tokenState.set(
            response.accessToken,
          );
          sessionStorage.removeItem(this.userKey);
          this.user.set({});
          this.profileLoaded.set(false);
        }),
        switchMap(() => this.ensureProfile()),
      );
  }

  me(): Observable<UserInfo> {
    return this.http.get<UserInfo>('/api/auth/me').pipe(
      tap((currentUser) => {
        this.user.set(currentUser);
        this.profileLoaded.set(true);

        sessionStorage.setItem(
          this.userKey,
          JSON.stringify(currentUser),
        );
      }),
    );
  }

  ensureProfile(force = false): Observable<UserInfo> {
    if (!this.tokenState()) {
      return throwError(
        () => new Error('AUTH_TOKEN_MISSING'),
      );
    }

    if (this.profileLoaded() && !force) {
      return of(this.user() as UserInfo);
    }

    if (this.profileRequest) {
      return this.profileRequest;
    }

    this.profileLoading.set(true);
    const request = this.me().pipe(
      finalize(() => {
        this.profileLoading.set(false);

        if (this.profileRequest === request) {
          this.profileRequest = undefined;
        }
      }),
      shareReplay({
        bufferSize: 1,
        refCount: false,
      }),
    );

    this.profileRequest = request;
    return request;
  }

  hasPermission(permission: string): boolean {
    return this.profileLoaded() &&
      (this.user().permissions?.includes(permission) ?? false);
  }

  changePassword(
    currentPassword: string,
    newPassword: string,
  ): Observable<unknown> {
    return this.http.put('/api/auth/change-password', {
      currentPassword,
      newPassword,
    });
  }

  logout(): void {
    this.http.post('/api/auth/logout', {}).subscribe({
      complete: () => this.clear(),
      error: () => this.clear(),
    });
  }

  clear(navigateToLogin = true): void {
    sessionStorage.removeItem(this.tokenKey);
    sessionStorage.removeItem(this.userKey);

    // Dọn token cũ của các bản frontend trước để tránh tự vào Dashboard.
    localStorage.removeItem(this.tokenKey);
    localStorage.removeItem(this.userKey);
    localStorage.removeItem('htbam_auth_version');

    this.tokenState.set(null);
    this.user.set({});
    this.profileLoaded.set(false);
    this.profileLoading.set(false);
    this.profileRequest = undefined;

    if (navigateToLogin) {
      void this.router.navigateByUrl('/login');
    }
  }

  private readInitialToken(): string | null {
    // Chỉ sessionStorage được dùng cho bản Angular hiện tại.
    localStorage.removeItem(this.tokenKey);
    localStorage.removeItem(this.userKey);
    localStorage.removeItem('htbam_auth_version');

    const token = sessionStorage.getItem(this.tokenKey);

    if (!token) {
      return null;
    }

    if (this.isJwtExpired(token)) {
      sessionStorage.removeItem(this.tokenKey);
      sessionStorage.removeItem(this.userKey);
      return null;
    }

    return token;
  }

  private isJwtExpired(token: string): boolean {
    try {
      const payload = token.split('.')[1];

      if (!payload) {
        return true;
      }

      const normalized = payload
        .replace(/-/g, '+')
        .replace(/_/g, '/');

      const decoded = JSON.parse(
        decodeURIComponent(
          Array.prototype.map
            .call(
              atob(normalized),
              (character: string) =>
                '%' +
                (
                  '00' +
                  character
                    .charCodeAt(0)
                    .toString(16)
                ).slice(-2),
            )
            .join(''),
        ),
      );

      if (!decoded.exp) {
        return false;
      }

      return Date.now() >= decoded.exp * 1000;
    } catch {
      return true;
    }
  }
}
