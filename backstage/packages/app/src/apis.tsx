import {
  ScmIntegrationsApi,
  scmIntegrationsApiRef,
  ScmAuth,
} from '@backstage/integration-react';
import {
  AnyApiFactory,
  BackstageIdentityApi,
  configApiRef,
  createApiFactory,
  createApiRef,
  discoveryApiRef,
  oauthRequestApiRef,
  OpenIdConnectApi,
  ProfileInfoApi,
  SessionApi,
} from '@backstage/core-plugin-api';
import { OAuth2 } from '@backstage/core-app-api';
import { ApiEntity } from '@backstage/catalog-model';

// The Logto OIDC sign-in API (ADR-IC-021, Boundary 6). The generic `oidc` provider has no built-in
// frontend apiRef (unlike github/google), so we mint one; its id must match the backend providerId
// 'oidc' (packages/backend/src/oidcGateProvider.ts) and the SignInPage provider id in App.tsx.
export const oidcAuthApiRef = createApiRef<
  OpenIdConnectApi & ProfileInfoApi & BackstageIdentityApi & SessionApi
>({ id: 'auth.oidc' });
import {
  ApiDefinitionWidget,
  apiDocsConfigRef,
  defaultDefinitionWidgets,
  PlainApiDefinitionWidget,
} from '@backstage/plugin-api-docs';

export const apis: AnyApiFactory[] = [
  createApiFactory({
    api: scmIntegrationsApiRef,
    deps: { configApi: configApiRef },
    factory: ({ configApi }) => ScmIntegrationsApi.fromConfig(configApi),
  }),
  ScmAuth.createDefaultApiFactory(),
  // Logto OIDC provider (ADR-IC-021). auth-code + PKCE against Logto's discovery; the session is
  // bound to auth.environment so it matches the backend's active provider environment.
  createApiFactory({
    api: oidcAuthApiRef,
    deps: {
      discoveryApi: discoveryApiRef,
      oauthRequestApi: oauthRequestApiRef,
      configApi: configApiRef,
    },
    factory: ({ discoveryApi, oauthRequestApi, configApi }) =>
      OAuth2.create({
        configApi,
        discoveryApi,
        oauthRequestApi,
        provider: {
          id: 'oidc',
          title: 'babelstone (Logto)',
          icon: () => null,
        },
        environment: configApi.getOptionalString('auth.environment'),
        defaultScopes: ['openid', 'profile', 'email'],
      }),
  }),
  // babelstone reconciliation api-docs widget (ADR-IC-015; discharges handoff
  // babelstone-ax0b.6). The catalogue carries `spec.type: reconciliation` API
  // entities that plugin-api-docs does not render natively, so its definition
  // tab would show raw text; this factory renders them as syntax-highlighted
  // YAML and delegates every other type to the plugin's default widgets.
  createApiFactory({
    api: apiDocsConfigRef,
    deps: {},
    factory: () => {
      const definitionWidgets = defaultDefinitionWidgets();
      return {
        getApiDefinitionWidget: (apiEntity: ApiEntity) => {
          // babelstone: spec.type: reconciliation -> syntax-highlighted YAML, not plain text.
          if (apiEntity.spec.type === 'reconciliation') {
            return {
              type: 'reconciliation',
              title: 'Reconciliation contract',
              rawLanguage: 'yaml',
              component: definition => (
                <PlainApiDefinitionWidget
                  definition={definition}
                  language="yaml"
                />
              ),
            } as ApiDefinitionWidget;
          }
          // every other type (asyncapi / openapi / graphql) keeps its native widget.
          return definitionWidgets.find(d => d.type === apiEntity.spec.type);
        },
      };
    },
  }),
];
