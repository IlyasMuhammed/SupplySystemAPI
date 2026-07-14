import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface CityModel {
  id: string;
  name: string;
  countryId: string;
  countryName: string;
  isActive: boolean;
}

export interface CountryDropDown {
  id: string;
  name: string;
}

export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

@Injectable({
  providedIn: 'root'
})
export class CitiesService {
  private readonly baseUrl = 'https://localhost:51800/api/lookups';

  constructor(private http: HttpClient) {}

  getAllCities(): Observable<ApiResponse<CityModel[]>> {
    return this.http.get<ApiResponse<CityModel[]>>(`${this.baseUrl}/cities`);
  }

  getCitiesByCountry(countryId: string): Observable<ApiResponse<CityModel[]>> {
    return this.http.get<ApiResponse<CityModel[]>>(`${this.baseUrl}/cities/by-country/${countryId}`);
  }

  createCity(data: { name: string; countryId: string }): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.baseUrl}/cities`, data);
  }

  updateCity(id: string, data: { name: string; countryId: string }): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.baseUrl}/cities/${id}`, data);
  }

  deleteCity(id: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.baseUrl}/cities/${id}`);
  }

  getCountries(): Observable<ApiResponse<CountryDropDown[]>> {
    return this.http.get<ApiResponse<CountryDropDown[]>>(`${this.baseUrl}/countries`);
  }
}
