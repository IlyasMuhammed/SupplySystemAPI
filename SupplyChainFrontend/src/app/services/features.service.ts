import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

const BASE = 'https://localhost:52800/api/system/features';

export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

export interface FeatureDefinitionModel {
  id: string;
  featureCode: string;
  featureName: string;
  category: 'MODULE' | 'SCREEN' | 'FEATURE';
  description?: string;
  isCore: boolean;
  displayOrder: number;
}

export interface PlanFeatureTemplateItem {
  featureCode: string;
  isEnabledByDefault: boolean;
}

export interface PlanFeatureTemplateModel {
  plan: string;
  features: PlanFeatureTemplateItem[];
}

@Injectable({ providedIn: 'root' })
export class FeaturesService {
  constructor(private http: HttpClient) {}

  getCatalog(): Observable<ApiResponse<FeatureDefinitionModel[]>> {
    return this.http.get<ApiResponse<FeatureDefinitionModel[]>>(BASE);
  }

  getPlanTemplates(): Observable<ApiResponse<PlanFeatureTemplateModel[]>> {
    return this.http.get<ApiResponse<PlanFeatureTemplateModel[]>>(`${BASE}/plan-templates`);
  }
}
