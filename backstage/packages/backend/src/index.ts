/*
 * Hi!
 *
 * Note that this is an EXAMPLE Backstage backend. Please check the README.
 *
 * Happy hacking!
 */

import { createBackend } from '@backstage/backend-defaults';
import { fileUrlReaderServiceFactory } from './fileUrlReader';
import { oidcGateProviderModule } from './oidcGateProvider';

const backend = createBackend();

// A read-only file:// URL reader so the catalogue's relative $text refs resolve against the baked
// tree (ADR-IC-015 §9). The stock backend ships no file: reader; this supplies it. See
// ./fileUrlReader.ts for why a runtime reader (not build-time inlining) is the conformant fix.
backend.add(fileUrlReaderServiceFactory);

backend.add(import('@backstage/plugin-app-backend'));
backend.add(import('@backstage/plugin-proxy-backend'));

// scaffolder plugin
backend.add(import('@backstage/plugin-scaffolder-backend'));
backend.add(import('@backstage/plugin-scaffolder-backend-module-github'));
backend.add(
  import('@backstage/plugin-scaffolder-backend-module-notifications'),
);

// techdocs plugin
backend.add(import('@backstage/plugin-techdocs-backend'));

// auth plugin
backend.add(import('@backstage/plugin-auth-backend'));
// See https://backstage.io/docs/backend-system/building-backends/migrating#the-auth-plugin
// guest — local-dev only (Backstage blocks it in production); kept so `yarn dev` needs no IdP.
backend.add(import('@backstage/plugin-auth-backend-module-guest-provider'));
// See https://backstage.io/docs/auth/guest/provider
// Logto OIDC gate (ADR-IC-021, Boundary 6) — the real sign-in on the deployed box. Registered ONLY
// when BACKSTAGE_AUTH_ENVIRONMENT=production (set by infra/k8s/overlays/staging/backstage-oidc.patch.yaml).
// The auth plugin eagerly initialises EVERY environment sub-block of a registered provider at
// startup (independent of auth.environment), so registering it unconditionally would make the image
// fail-closed-crash anywhere the OIDC client secret / Logto discovery isn't present (local runs,
// `make demo`). Gating registration keeps the image guest-by-default there, and fail-closed on the
// deployed box (a missing BACKSTAGE_OIDC_CLIENT_SECRET stops the pod). See ./oidcGateProvider.ts.
if (process.env.BACKSTAGE_AUTH_ENVIRONMENT === 'production') {
  backend.add(oidcGateProviderModule);
}

// catalog plugin
backend.add(import('@backstage/plugin-catalog-backend'));
backend.add(
  import('@backstage/plugin-catalog-backend-module-scaffolder-entity-model'),
);

// See https://backstage.io/docs/features/software-catalog/configuration#subscribing-to-catalog-errors
backend.add(import('@backstage/plugin-catalog-backend-module-logs'));

// permission plugin
backend.add(import('@backstage/plugin-permission-backend'));
// See https://backstage.io/docs/permissions/getting-started for how to create your own permission policy
backend.add(
  import('@backstage/plugin-permission-backend-module-allow-all-policy'),
);

// search plugin
backend.add(import('@backstage/plugin-search-backend'));

// search engine
// See https://backstage.io/docs/features/search/search-engines
backend.add(import('@backstage/plugin-search-backend-module-pg'));

// search collators
backend.add(import('@backstage/plugin-search-backend-module-catalog'));
backend.add(import('@backstage/plugin-search-backend-module-techdocs'));

// kubernetes plugin
backend.add(import('@backstage/plugin-kubernetes-backend'));

// notifications and signals plugins
backend.add(import('@backstage/plugin-notifications-backend'));
backend.add(import('@backstage/plugin-signals-backend'));

// mcp actions plugin
backend.add(import('@backstage/plugin-mcp-actions-backend'));

backend.start();
