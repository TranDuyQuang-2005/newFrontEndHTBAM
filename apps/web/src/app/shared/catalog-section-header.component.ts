import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

@Component({
  selector:'app-catalog-section-header',standalone:true,changeDetection:ChangeDetectionStrategy.OnPush,
  template:`<section class="catalog-section-header" [attr.aria-label]="title"><div><h2>{{title}}</h2><p>{{description}}</p></div><div class="catalog-section-actions"><ng-content /></div></section>`,
})
export class CatalogSectionHeaderComponent {
  @Input({required:true}) title='';
  @Input() description='';
}
