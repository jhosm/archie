/*
 * A GATE-ONLY OIDC sign-in provider for the babelstone catalogue portal
 * (ADR-IC-021 — Logto as the single OAuth/OIDC issuer; Boundary 6, the developer/ops surface).
 *
 * In plain English: the portal delegates login to Logto (auth.babelstone.dev) and treats a
 * successful Logto authentication as sufficient to enter — Logto is purely the ACCESS GATE. We
 * deliberately do NOT look the user up in the software catalogue: the catalogue holds no
 * `kind: User` / `kind: Group` entities and stores no personal identity data, so mirroring users
 * in is exactly what would re-trigger the deferred GDPR data-inventory obligation (bd
 * babelstone-zla1.6.8 / ADR-IC-015 Residual Risk). Instead this resolver mints a Backstage
 * identity token straight from the OIDC subject (`sub`) — which every Logto token carries — with
 * no catalog lookup and no user record created.
 *
 * This mirrors how Grafana and Mission Control authenticate against Logto directly (ADR-IC-021
 * §Decision) — one identity source, no second user store. It replaces the default OIDC provider
 * (@backstage/plugin-auth-backend-module-oidc-provider), whose stock sign-in resolvers all expect
 * a matching catalog User; we register our own provider under the same `oidc` id and reuse only its
 * `oidcAuthenticator` (the OAuth/OIDC protocol machinery).
 */
import { createBackendModule } from '@backstage/backend-plugin-api';
import {
  authProvidersExtensionPoint,
  createOAuthProviderFactory,
} from '@backstage/plugin-auth-node';
import { oidcAuthenticator } from '@backstage/plugin-auth-backend-module-oidc-provider';
import { DEFAULT_NAMESPACE, stringifyEntityRef } from '@backstage/catalog-model';

export const oidcGateProviderModule = createBackendModule({
  pluginId: 'auth', // targets the auth plugin
  moduleId: 'oidc-gate-provider',
  register(reg) {
    reg.registerInit({
      deps: { providers: authProvidersExtensionPoint },
      async init({ providers }) {
        providers.registerProvider({
          // Must match `auth.providers.oidc` in app-config and the frontend apiRef id 'oidc'.
          providerId: 'oidc',
          factory: createOAuthProviderFactory({
            authenticator: oidcAuthenticator,
            async signInResolver(info, ctx) {
              const sub = info.result.fullProfile.userinfo.sub;
              if (!sub) {
                throw new Error(
                  'Logto OIDC userinfo carried no `sub` — cannot mint a gate identity',
                );
              }
              // Gate-only: the Logto subject IS the Backstage user ref. No catalog User entity is
              // required or created — Logto authenticated the principal, which is all the gate asks.
              const userRef = stringifyEntityRef({
                kind: 'User',
                name: sub,
                namespace: DEFAULT_NAMESPACE,
              });
              return ctx.issueToken({
                claims: { sub: userRef, ent: [userRef] },
              });
            },
          }),
        });
      },
    });
  },
});
