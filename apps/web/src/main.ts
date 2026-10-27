import 'zone.js';
import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { AppComponent } from './app/app.component';

bootstrapApplication(AppComponent, appConfig).catch(
  (error: unknown) => {
    console.error('Không thể khởi động giao diện', error);

    document.body.replaceChildren();

    const main = document.createElement('main');
    main.style.fontFamily = 'Arial, sans-serif';
    main.style.padding = '32px';
    main.style.maxWidth = '900px';
    main.style.margin = 'auto';

    const title = document.createElement('h1');
    title.textContent =
      'Không thể khởi động Hệ thống giám sát';

    const description = document.createElement('p');
    description.textContent =
      'Giao diện chưa thể khởi tạo. Vui lòng thử lại.';

    const notice = document.createElement('p');
    notice.style.background = '#fee';
    notice.style.padding = '16px';
    notice.style.borderRadius = '8px';
    notice.textContent =
      'Không thể tải giao diện. Vui lòng tải lại trang hoặc liên hệ quản trị hệ thống.';

    const brand = document.createElement('a');
    brand.href = '/';
    brand.className = 'compact-brand';
    brand.title = 'Hệ thống giám sát lớp học - Khoa Công nghệ Thông tin HUIT';
    const logo = document.createElement('img');
    logo.src = 'assets/huit-logo.png';
    logo.alt = 'Logo HUIT';
    logo.className = 'compact-brand__logo';
    logo.width = logo.height = 26;
    const name = document.createElement('span');
    name.className = 'compact-brand__name';
    name.textContent = 'Hệ thống giám sát';
    brand.append(logo, name);

    main.append(brand, title, description, notice);
    document.body.append(main);
  },
);
