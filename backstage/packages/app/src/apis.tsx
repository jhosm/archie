import {
  ScmIntegrationsApi,
  scmIntegrationsApiRef,
  ScmAuth,
} from '@backstage/integration-react';
import {
  AnyApiFactory,
  configApiRef,
  createApiFactory,
} from '@backstage/core-plugin-api';
import { ApiEntity } from '@backstage/catalog-model';
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
