// Used as-is for `ng serve` / development builds. Swapped for environment.prod.ts at build time
// (see angular.json's production fileReplacements) for `ng build` / production builds.
export const environment = {
  production: false,
  // Rooted at /api — used by almost every service (`${environment.apiUrl}/lookups`, etc).
  apiUrl: 'https://localhost:52800/api',
  // Bare origin, no /api suffix — needed for resolving relative file URLs the backend returns
  // (attachments, uploads) and for the couple of services that build their own /api/... paths.
  apiOrigin: 'https://localhost:52800'
};
